using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.Storage;

namespace BiliCopilot.UI.SponsorBlock.Application;

internal sealed partial class SponsorApplicationCoordinator
{
    private sealed record SubmissionWork(ClientEpoch Epoch, Task<WriteResult> Task,
        SubmissionSnapshot Submission, MediaIdentity Media);
    private sealed record JournalWork(ClientEpoch Epoch, Task<WriteResult> Task, MutationRecord Record);
    private bool StillIntent(PlayerTicket ticket) => !Stopping && settings.NetworkEnabled
        && players.TryGetValue(ticket.Attachment, out var state) && !state.Attachment.IsClosed
        && state.Attachment.Latest?.Stamp is { } current && SponsorPlayerAttachment.SameVisit(ticket.Stamp, current)
        && current.PolicyRevision == policyRevision;
    private static ApplicationResult NotSent(string reason, PersistenceStatus? status = null) =>
        new(ApplicationStatus.Unavailable, reason, status, new(WriteStatus.NotSent, Reason: reason));
    private void RecordCompletion(string id, MediaIdentity media, ManualOperation operation, WriteResult result, bool persisted)
    {
        if (mutations.Length == 128) mutations = mutations.RemoveAt(0);
        mutations = mutations.Add(new(id, media, operation, result.Status, persisted, result.Reason));
    }

    /// <summary>Only an explicit Submit button calls this. expectedPublicId comes from an explicit
    /// identity view/load, never from a Bili account. Identity is load-only and not stored in UI records.</summary>
    internal async Task<ApplicationResult> SubmitAsync(PlayerTicket ticket, string expectedPublicId)
    {
        ApplicationResult? denied = null;
        var work = await OnOwner(async () =>
        {
            if (!Current(ticket, out var state) || ticket.DraftRevision != draftRevision || !settings.NetworkEnabled || requests.Count >= 16)
            { denied = NotSent("StaleDisabledOrBusy"); return null; }
            if (state.Attachment.Latest?.Actual?.Duration is not double duration || !double.IsFinite(duration) || duration <= 0)
            { denied = NotSent("ActualDurationRequired"); return null; }
            var loaded = await store.LoadIdentityAsync(); // NEVER creates on missing/platform failure.
            if (!loaded.Success || loaded.Value!.PublicId != expectedPublicId)
            { denied = NotSent("ExplicitCurrentIdentityRequired", loaded.Status); return null; }
            if (!StillIntent(ticket)) { denied = NotSent("OwnerPolicyOrVisitChanged"); return null; }
            var media = ticket.Stamp.Owner.Media;
            var prepared = queue.PrepareSubmission(media, duration);
            if (!prepared.Success) { denied = NotSent(prepared.Error ?? "PreparationFailed"); return null; }
            ++draftRevision; dirty = true;
            var saved = await SaveAsync(settings);
            if (saved != PersistenceStatus.Success || !StillIntent(ticket))
            {
                var notSent = new WriteResult(WriteStatus.NotSent, Reason: saved == PersistenceStatus.Success ? "OwnerRetiredBeforeDispatch" : "PreparationNotDurable");
                queue.CompleteSubmission(prepared.Submission!.OperationId, media, notSent);
                ++draftRevision; dirty = true;
                // If the first commit succeeded, retire its persisted in-flight record too. Never send on failure.
                if (saved == PersistenceStatus.Success) await SaveAsync(settings);
                RecordCompletion(prepared.Submission.OperationId, media, ManualOperation.Submit, notSent, !dirty);
                Publish(); denied = NotSent(notSent.Reason!, saved); return null;
            }
            ClientEpoch epoch;
            try { epoch = AcquireClient(); }
            catch
            {
                var notSent = new WriteResult(WriteStatus.NotSent, Reason: "ClientAdmissionUnavailable");
                queue.CompleteSubmission(prepared.Submission!.OperationId, media, notSent);
                ++draftRevision; dirty = true;
                var completionSaved = await SaveAsync(settings);
                RecordCompletion(prepared.Submission.OperationId, media, ManualOperation.Submit, notSent, completionSaved == PersistenceStatus.Success);
                Publish(); denied = NotSent(notSent.Reason!, completionSaved); return null;
            }
            Task<WriteResult> task;
            try
            {
                task = epoch.Client.SubmitAsync(media, prepared.Submission!.Payload, duration,
                    loaded.Value.PrivateIdForExplicitRequest(), ManualWriteIntent.Capture(media, ManualOperation.Submit));
            }
            catch { task = Task.FromResult(new WriteResult(WriteStatus.Unknown, Reason: "ClientFactoryOutcomeUnknown")); }
            requests.Add(task); Publish();
            return new SubmissionWork(epoch, task, prepared.Submission!, media);
        });
        if (work is null) return denied ?? NotSent("NotPrepared");
        WriteResult result;
        try { result = await work.Task.ConfigureAwait(false); }
        catch { result = new(WriteStatus.Unknown, Reason: "ClientOutcomeUnknown"); }
        return await OnOwner<ApplicationResult>(async () =>
        {
            // No current-owner check here: only the captured original operation/revision may be completed.
            var completed = queue.CompleteSubmission(work.Submission.OperationId, work.Media, result);
            ++draftRevision; dirty = true;
            var saved = await SaveAsync(settings);
            requests.Remove(work.Task); ReleaseClient(work.Epoch);
            RecordCompletion(work.Submission.OperationId, work.Media, ManualOperation.Submit, result, saved == PersistenceStatus.Success);
            Publish();
            return new(saved == PersistenceStatus.Success ? ApplicationStatus.Success : ApplicationStatus.PersistenceFailure,
                saved == PersistenceStatus.Success ? null : "CompletionNotDurableDoNotReplay", saved, result, completed);
        });
    }

    /// <summary>Non-Q4 requests first commit a captured preparation through the default owner-bound
    /// store journal (or an explicitly supplied compatible journal). Recovered facts cannot enter here.</summary>
    internal async Task<ApplicationResult> MutateAsync(MutationIntent intent, string? expectedPublicId = null)
    {
        ApplicationResult? denied = null;
        var work = await OnOwner(async () =>
        {
            var ticket = intent.Ticket;
            if (!Enum.IsDefined(intent.Operation) || intent.Operation == ManualOperation.Submit)
            { denied = NotSent("UseQ4Submit"); return null; }
            if (!Current(ticket, out var state) || ticket.DraftRevision != draftRevision || !settings.NetworkEnabled || requests.Count >= 16)
            { denied = NotSent("StaleDisabledOrBusy"); return null; }
            if (intent.Operation == ManualOperation.ReportViewed && !settings.ViewedTelemetryEnabled)
            { denied = NotSent("ViewedTelemetryDisabled"); return null; }
            if (intent.Text?.Length > 128 || intent.Text?.Any(char.IsControl) == true || !Enum.IsDefined(intent.Vote))
            { denied = NotSent("InvalidInput"); return null; }
            var target = intent.Target is null ? null : state.Raw.FirstOrDefault(s => s.Uuid == intent.Target.Uuid && s.Source == intent.Target.Source
                && intent.Target.Media == ticket.Stamp.Owner.Media);
            if (intent.Operation is ManualOperation.Vote or ManualOperation.ChangeCategory or ManualOperation.ReportViewed && target is null)
            { denied = NotSent("CurrentExactTargetRequired"); return null; }
            if (intent.Port is { } port && (port.BvId != ticket.Stamp.Owner.Media.BvId || port.Cid != ticket.Stamp.Owner.Media.Cid))
            { denied = NotSent("ForeignPortIdentity"); return null; }
            var duration = state.Attachment.Latest?.Actual?.Duration;
            if (intent.Operation == ManualOperation.BindPort && (duration is null || !double.IsFinite(duration.Value) || duration <= 0))
            { denied = NotSent("ActualTargetDurationRequired"); return null; }
            if (intent.Operation == ManualOperation.ReportViewed && !HasConfirmedTarget(state, intent.Target!))
            { denied = NotSent("ConfirmedReceiptRequired"); return null; }
            if (intent.Operation is ManualOperation.BindPort or ManualOperation.VotePort or ManualOperation.UpdatePort)
            {
                // A mapping mutation invalidates previously asserted source2 authority before dispatch.
                state.Aligned = null; await PushAsync(state);
            }
            SponsorLocalIdentity? identity = null;
            if (intent.Operation is not (ManualOperation.UpdatePort or ManualOperation.ReportViewed))
            {
                var loaded = await store.LoadIdentityAsync();
                if (!loaded.Success || loaded.Value!.PublicId != expectedPublicId)
                { denied = NotSent("ExplicitCurrentIdentityRequired", loaded.Status); return null; }
                identity = loaded.Value;
            }
            if (!StillIntent(ticket)) { denied = NotSent("OwnerRetiredBeforePreparation"); return null; }
            var record = new MutationRecord(Guid.NewGuid().ToString("N"), ticket.Attachment, ticket.Stamp,
                ticket.DraftRevision, intent.Operation, intent.Target, intent.Vote, intent.Text, intent.Port,
                Api.SponsorClient.Service.AbsoluteUri, duration, target, identity?.PublicId);
            PersistenceStatus prepared;
            try { prepared = await CheckInjectedJournalVersionAsync(await journal.BeforeSendAsync(record)); }
            catch { prepared = PersistenceStatus.IoFailure; }
            if (prepared != PersistenceStatus.Success)
            { SetAuxiliaryStatus(prepared); Publish(); denied = NotSent("PreparationNotDurable", prepared); return null; }
            if (!StillIntent(ticket))
            {
                var notSent = new WriteResult(WriteStatus.NotSent, Reason: "OwnerRetiredBeforeDispatch");
                var persisted = await CompleteJournalAsync(record, notSent);
                RecordCompletion(record.Id, ticket.Stamp.Owner.Media, intent.Operation, notSent, persisted == PersistenceStatus.Success);
                Publish(); denied = NotSent(notSent.Reason!, persisted); return null;
            }
            ClientEpoch epoch;
            try { epoch = AcquireClient(); }
            catch
            {
                var notSent = new WriteResult(WriteStatus.NotSent, Reason: "ClientAdmissionUnavailable");
                var completionSaved = await CompleteJournalAsync(record, notSent);
                RecordCompletion(record.Id, ticket.Stamp.Owner.Media, intent.Operation, notSent, completionSaved == PersistenceStatus.Success);
                Publish(); denied = NotSent(notSent.Reason!, completionSaved); return null;
            }
            var media = ticket.Stamp.Owner.Media;
            var token = ManualWriteIntent.Capture(media, intent.Operation);
            Task<WriteResult> task;
            try
            {
                task = intent.Operation switch
                {
                    ManualOperation.Vote => epoch.Client.VoteSegmentAsync(media, target!.Uuid, intent.Vote, identity!.PrivateIdForExplicitRequest(), token),
                    ManualOperation.ChangeCategory => epoch.Client.ChangeCategoryAsync(media, target!, intent.Text ?? "", identity!.PrivateIdForExplicitRequest(), token),
                    ManualOperation.SetUsername => epoch.Client.SetUsernameAsync(media, intent.Text ?? "", identity!.PrivateIdForExplicitRequest(), token),
                    ManualOperation.BindPort => epoch.Client.BindPortAsync(media, intent.Text ?? "", duration!.Value, identity!.PrivateIdForExplicitRequest(), token),
                    ManualOperation.VotePort when intent.Port is not null => epoch.Client.VotePortAsync(media, intent.Port, intent.Vote, identity!.PrivateIdForExplicitRequest(), token),
                    ManualOperation.UpdatePort when intent.Port is not null => epoch.Client.UpdatePortedSegmentsAsync(media, intent.Port, token),
                    ManualOperation.ReportViewed => epoch.Client.ReportViewedAsync(media, target!.Uuid, token),
                    _ => Task.FromResult(new WriteResult(WriteStatus.NotSent, Reason: "UnsupportedOrMissingTarget")),
                };
            }
            catch { task = Task.FromResult(new WriteResult(WriteStatus.Unknown, Reason: "ClientFactoryOutcomeUnknown")); }
            requests.Add(task); Publish(); return new JournalWork(epoch, task, record);
        });
        if (work is null) return denied ?? NotSent("NotPrepared");
        WriteResult result;
        try { result = await work.Task.ConfigureAwait(false); }
        catch { result = new(WriteStatus.Unknown, Reason: "ClientOutcomeUnknown"); }
        return await OnOwner<ApplicationResult>(async () =>
        {
            var saved = await CompleteJournalAsync(work.Record, result);
            requests.Remove(work.Task); ReleaseClient(work.Epoch);
            RecordCompletion(work.Record.Id, work.Record.Stamp.Owner.Media, work.Record.Operation, result, saved == PersistenceStatus.Success);
            // No auto refetch/replay or alignment. Caller explicitly fetches the exact target after a port mutation.
            Publish(); return new(saved == PersistenceStatus.Success ? ApplicationStatus.Success : ApplicationStatus.PersistenceFailure,
                saved == PersistenceStatus.Success ? null : "CompletionNotDurableDoNotReplay", saved, result);
        });
    }
    private async Task<PersistenceStatus> CompleteJournalAsync(MutationRecord record, WriteResult result)
    {
        try { return await CheckInjectedJournalVersionAsync(await journal.CompleteAsync(record, result)); }
        catch { return SetAuxiliaryStatus(PersistenceStatus.IoFailure); }
    }
    internal async Task<PortMutationResult> MutatePortAndRefreshAsync(MutationIntent intent, string? expectedPublicId = null)
    {
        if (intent.Operation is not (ManualOperation.BindPort or ManualOperation.VotePort or ManualOperation.UpdatePort))
            return new(NotSent("PortOperationRequired"));
        var mutation = await MutateAsync(intent, expectedPublicId);
        if (mutation.Write?.Status != WriteStatus.Accepted) return new(mutation);
        var current = await OnOwner(() =>
        {
            if (!StillIntent(intent.Ticket) || !players.TryGetValue(intent.Ticket.Attachment, out var state)) return null;
            // Binding/server update is not evidence of source2 coordinate alignment.
            state.Aligned = null;
            return new PlayerTicket(intent.Ticket.Attachment, state.Attachment.Latest!.Stamp!, draftRevision);
        });
        if (current is null) return new(mutation, new(ApplicationStatus.Stale));
        var refreshed = await ReadRelatedAsync(current, intent.Ticket.Stamp.Owner.Media, refresh: true);
        if (refreshed.View is { } view)
            await OnOwner(async () =>
            {
                if (!StillIntent(current) || !players.TryGetValue(current.Attachment, out var state)) return false;
                ++state.ReadEpoch; state.ReadStatus = view.Segments.Status; state.Aligned = null;
                if (view.Segments.IsSuccess) state.Raw = view.Segments.Value;
                await PushAsync(state); Publish(); return true;
            });
        return new(mutation, refreshed); // exact target server times; never append a foreign timeline.
    }
    private static bool HasConfirmedTarget(PlayerState state, PlaybackSegmentKey target) =>
        state.Attachment.Latest?.Applied is { WasPreview: false } effect && effect.Owner == state.Result?.Owner
        && effect.Contributors.Any(c => c.Key == target)
        || state.Attachment.Latest?.RateReceipt is { } rate && rate.Owner == state.Result?.Owner
        && rate.ObservedCoverage.Any(c => c.Contributors.Any(p => p.Key == target));
}
