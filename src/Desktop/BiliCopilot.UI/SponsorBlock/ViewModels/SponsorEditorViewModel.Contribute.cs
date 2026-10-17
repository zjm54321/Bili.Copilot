// Copyright (c) Bili Copilot. All rights reserved.

using System.Collections.Immutable;
using System.Globalization;
using BiliCopilot.UI.SponsorBlock.Application;
using BiliCopilot.UI.SponsorBlock.Models;
using BiliCopilot.UI.SponsorBlock.Persistence;
using BiliCopilot.UI.SponsorBlock.Rules;
using BiliCopilot.UI.SponsorBlock.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BiliCopilot.UI.SponsorBlock.ViewModels;

/// <summary>
/// 提交、重新允许提交、锁定分类、用户名与搬运关联。全部是显式用户动作；
/// 写操作先显示确认，再用当时读取的公开 ID 调用门面。结果未知时绝不重发。
/// </summary>
public sealed partial class SponsorEditorViewModel
{
    private string? _publicId;
    private PortVideo? _port;
    private Func<Task>? _pendingWrite;

    [ObservableProperty]
    private bool _hasIdentity;

    [ObservableProperty]
    private string _identityText = "正在读取贡献者身份…";

    [ObservableProperty]
    private int _readyCount;

    [ObservableProperty]
    private bool _canSubmit;

    [ObservableProperty]
    private string _submitText = "提交";

    [ObservableProperty]
    private string _submitHint = string.Empty;

    [ObservableProperty]
    private string _lastSubmitText = string.Empty;

    [ObservableProperty]
    private bool _hasLastSubmit;

    [ObservableProperty]
    private bool _isWriteConfirmVisible;

    [ObservableProperty]
    private string _writeConfirmTitle = string.Empty;

    [ObservableProperty]
    private string _writeConfirmText = string.Empty;

    [ObservableProperty]
    private string _writeConfirmButtonText = "确认";

    [ObservableProperty]
    private bool _isLockedBusy;

    [ObservableProperty]
    private string _lockedText = string.Empty;

    [ObservableProperty]
    private string _usernameInput = string.Empty;

    [ObservableProperty]
    private bool _canSetUsername;

    [ObservableProperty]
    private string _usernameNote = string.Empty;

    [ObservableProperty]
    private bool _canMutatePort;

    [ObservableProperty]
    private string _portNote = string.Empty;

    [ObservableProperty]
    private bool _isPortBusy;

    [ObservableProperty]
    private bool _hasPort;

    [ObservableProperty]
    private string _portStatusText = "可以查询这个视频是否关联了 YouTube 原视频";

    [ObservableProperty]
    private string _portDetailText = string.Empty;

    [ObservableProperty]
    private string _portSegmentsText = string.Empty;

    [ObservableProperty]
    private string _mappingText = string.Empty;

    [ObservableProperty]
    private string _youtubeIdInput = string.Empty;

    private void ApplyContributeAvailability()
    {
        var journal = Feature(SponsorFeature.SetUsername);
        CanSetUsername = journal?.Available == true;
        UsernameNote = CanSetUsername ? "用户名会公开显示在空降助手上" : SponsorUiText.Availability(journal, "暂不可用");
        var port = Feature(SponsorFeature.BindPort);
        CanMutatePort = port?.Available == true;
        PortNote = CanMutatePort ? "关联、投票和更新都会发送到空降助手服务器" : SponsorUiText.Availability(port, "暂不可用");
    }

    private void ResetContribute()
    {
        _port = null;
        _pendingWrite = null;
        IsWriteConfirmVisible = false;
        HasPort = false;
        PortDetailText = string.Empty;
        PortSegmentsText = string.Empty;
        MappingText = string.Empty;
        PortStatusText = "可以查询这个视频是否关联了 YouTube 原视频";
        LockedText = string.Empty;
        HasLastSubmit = false;
    }

    private void ProjectContribute(SponsorApplicationView view, IReadOnlyList<DraftEntry> mine)
    {
        ReadyCount = mine.Count(d => d.State == DraftState.Ready);
        var inFlight = mine.Any(d => d.State == DraftState.SubmissionInFlight);
        var network = view.Settings.NetworkEnabled;
        CanSubmit = ReadyCount > 0 && HasIdentity && network && !inFlight;
        SubmitText = ReadyCount > 0 ? $"提交 {ReadyCount} 个片段" : "提交";
        SubmitHint = inFlight ? "正在提交，请等待结果"
            : !network ? "需要先在设置中允许联网"
            : !HasIdentity ? "需要先在设置中创建贡献者身份"
            : ReadyCount == 0 ? "没有待提交的草稿"
            : string.Empty;
    }

    [RelayCommand]
    private Task LoadIdentityAsync()
        => SponsorUiWork.Track(this, LoadIdentityWorkAsync());

    private async Task LoadIdentityWorkAsync()
    {
        var epoch = _epoch;
        if (_app is not { } app)
        {
            return;
        }

        try
        {
            var identity = await app.LoadIdentityViewAsync(); // local, load-only
            if (epoch != _epoch)
            {
                return;
            }

            _publicId = identity.Status == PersistenceStatus.Success ? identity.PublicId : null;
            HasIdentity = !string.IsNullOrEmpty(_publicId);
            IdentityText = HasIdentity
                ? "贡献者 ID：" + SponsorUiText.ShortId(_publicId)
                : identity.Status == PersistenceStatus.Missing ? "尚未创建贡献者身份（在设置中创建）" : SponsorUiText.Persistence(identity.Status);
            if (_feed?.Current is { } view && _owner is { } owner)
            {
                ProjectContribute(view, view.Drafts.Where(d => d.Draft.MediaKey == owner.Media.Combined).ToList());
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                IdentityText = "暂时无法读取贡献者身份";
            }
        }
    }

    private void Confirm(string title, string text, string button, Func<Task> write)
    {
        WriteConfirmTitle = title;
        WriteConfirmText = text;
        WriteConfirmButtonText = button;
        _pendingWrite = write;
        IsWriteConfirmVisible = true;
    }

    [RelayCommand]
    private void CancelWrite()
    {
        _pendingWrite = null;
        IsWriteConfirmVisible = false;
    }

    [RelayCommand]
    private Task ConfirmWriteAsync()
        => SponsorUiWork.Track(this, ConfirmWriteWorkAsync());

    private async Task ConfirmWriteWorkAsync()
    {
        var write = _pendingWrite;
        _pendingWrite = null;
        IsWriteConfirmVisible = false;
        if (write is not null)
        {
            await write();
        }
    }

    // UI-local reason: the submission confirmation no longer matches the current drafts; nothing was sent.
    private const string ConfirmedDraftsChanged = "ConfirmedDraftsChanged";

    [RelayCommand]
    private void RequestSubmit()
    {
        if (!CanSubmit || _publicId is not { } publicId || _owner is not { } owner || _feed?.Current is not { } view)
        {
            Feedback(SubmitHint);
            return;
        }

        var ready = view.Drafts.Where(d => d.Draft.MediaKey == owner.Media.Combined && d.State == DraftState.Ready).ToList();
        var lines = string.Join("、", ready.Take(5).Select(d => $"{SponsorUiText.CategoryTitle(d.Draft.Segment.Category)} {SponsorUiText.Range(d.Draft.Segment)}"));
        if (ready.Count > 5)
        {
            lines += $" 等 {ready.Count} 个";
        }

        var locked = ready.Any(d => _locked.Contains(d.Draft.Segment.Category)) ? "其中有已锁定的分类，可能会被拒绝。" : string.Empty;

        // The confirmed authority is the DraftRevision of the SAME immutable view that rendered the list above
        // (plus owner/publicId). Draft revisions are monotonic: any edit/add/delete/ready change after this point
        // gives a different revision, and the confirmation then no longer describes what would be sent.
        var confirmedDraftRevision = view.DraftRevision;
        Confirm(
            "提交到空降助手？",
            $"将提交：{lines}。{locked}使用贡献者 ID {SponsorUiText.ShortId(publicId)}。提交后不能撤回；如果结果未知，不会自动重发。",
            "提交",
            () => SubmitCoreAsync(owner, publicId, confirmedDraftRevision));
    }

    private Task SubmitCoreAsync(PlaybackOwner owner, string publicId, long confirmedDraftRevision)
        => SponsorUiWork.Track(this, SubmitCoreWorkAsync(owner, publicId, confirmedDraftRevision));

    private async Task SubmitCoreWorkAsync(PlaybackOwner owner, string publicId, long confirmedDraftRevision)
    {
        var epoch = _epoch;
        if (_app is not { } app || owner != _owner)
        {
            Feedback(SponsorUiText.Reason("MediaChanged", "视频已切换"));
            return;
        }

        IsBusy = true;
        try
        {
            // Every invocation (including the helper's single unadmitted-stale refresh) must still carry the
            // CONFIRMED draft revision; the ticket's owner is checked by the helper. A different revision is
            // refused here, before N4 prepares or sends anything. Authority is never advanced to the newer
            // revision. N4's own revision check then covers changes after this capture.
            var result = await SponsorTickets.RunAsync(app, _attachment, owner, ticket => ticket.DraftRevision == confirmedDraftRevision
                ? app.SubmitAsync(ticket, publicId)
                : Task.FromResult(new ApplicationResult(ApplicationStatus.Stale, ConfirmedDraftsChanged)));

            // Q4 already recorded the completion against the ORIGINAL operation; this banner is only for the same owner.
            if (epoch != _epoch || owner != _owner)
            {
                return;
            }

            if (result is { Status: ApplicationStatus.Stale, Reason: ConfirmedDraftsChanged })
            {
                LastSubmitText = "未提交：确认之后草稿有了变化。请重新点击“提交”，确认新的内容。";
                HasLastSubmit = true;
                return;
            }

            var write = result.Write?.Status;
            LastSubmitText = write switch
            {
                WriteStatus.Accepted => "服务器已接受提交（每条是否确认见草稿状态）",
                WriteStatus.Rejected => "服务器拒绝了提交" + (result.Write?.HttpStatus is int code ? $"（{code.ToString(CultureInfo.InvariantCulture)}）" : string.Empty),
                WriteStatus.Unknown => "提交结果未知。不会自动重发，草稿已标为“结果未知”。",
                WriteStatus.NotSent => "未发送：" + SponsorUiText.Result(result),
                _ => SponsorUiText.Result(result),
            };
            if (result.Status == ApplicationStatus.PersistenceFailure && write is not WriteStatus.NotSent)
            {
                LastSubmitText += "（结果未能保存到本机，不会重发）";
            }

            HasLastSubmit = true;
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                LastSubmitText = "提交没有完成。不会自动重发，请查看草稿状态。";
                HasLastSubmit = true;
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsBusy = false;
            }
        }
    }

    internal Task RetryAsync(SponsorDraftItem item)
        => SponsorUiWork.Track(this, RetryWorkAsync(item));

    private async Task RetryWorkAsync(SponsorDraftItem item)
    {
        if (item.Entry is not { } entry || !item.CanRetry)
        {
            return;
        }

        if (item.NeedsRiskAcknowledgement && !item.IsRiskAcknowledged)
        {
            Feedback(SponsorUiText.Reason("DuplicateRiskAcknowledgementRequired", "请先确认"));
            return;
        }

        var epoch = _epoch;
        var owner = _owner;
        if (_app is not { } app || owner is null)
        {
            return;
        }

        var selection = new DraftSelection(item.Key, entry.Draft.Revision);
        var acknowledged = item.IsRiskAcknowledged;
        var result = await SponsorTickets.RunAsync(app, _attachment, owner, ticket => app.RetryDraftAsync(ticket, selection, acknowledged));
        if (epoch == _epoch && owner == _owner)
        {
            Feedback(result.Status == ApplicationStatus.Success
                ? "已重新允许提交。确认无误后，请再次点击“提交”。"
                : SponsorUiText.Result(result));
        }
    }

    [RelayCommand]
    private Task ReadLockedAsync()
        => SponsorUiWork.Track(this, ReadLockedWorkAsync());

    private async Task ReadLockedWorkAsync()
    {
        var epoch = _epoch;
        var owner = _owner;
        if (_app is not { } app || owner is null || SponsorTickets.Read(app, _attachment) is not { } ticket || ticket.Stamp.Owner != owner)
        {
            return;
        }

        IsLockedBusy = true;
        try
        {
            var read = await app.ReadLockedCategoriesAsync(ticket);
            if (epoch != _epoch || owner != _owner)
            {
                return;
            }

            if (read.IsSuccess)
            {
                var locked = read.Value.IsDefault ? [] : read.Value.ToImmutableHashSet(StringComparer.Ordinal);
                SetLockedCategories(locked);
                LockedText = locked.Count == 0
                    ? "这个视频没有锁定的分类"
                    : "已锁定：" + string.Join("、", locked.Select(SponsorUiText.CategoryTitle)) + "。这些分类的新提交可能被拒绝。";
            }
            else
            {
                LockedText = read.Status == ReadStatus.Disabled ? "需要先在设置中允许联网" : "查询失败：" + SponsorUiText.Read(read.Status);
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                LockedText = "查询失败";
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsLockedBusy = false;
            }
        }
    }

    [RelayCommand]
    private void RequestSetUsername()
    {
        var name = UsernameInput.Trim();
        if (!CanSetUsername || _publicId is not { } publicId || _owner is not { } owner)
        {
            Feedback(UsernameNote);
            return;
        }

        if (name.Length is 0 or > 64 || name.Any(char.IsControl))
        {
            Feedback("用户名需要 1–64 个字符，且不能包含控制字符");
            return;
        }

        Confirm("设置公开用户名？", $"将把贡献者 ID {SponsorUiText.ShortId(publicId)} 的用户名设为“{name}”，所有人都能看到。", "设置",
            () => MutateCoreAsync(owner, ManualOperation.SetUsername, publicId, text: name));
    }

    [RelayCommand]
    private Task ReadPortAsync()
        => SponsorUiWork.Track(this, ReadPortWorkAsync());

    private async Task ReadPortWorkAsync()
    {
        var epoch = _epoch;
        var owner = _owner;
        if (_app is not { } app || owner is null || SponsorTickets.Read(app, _attachment) is not { } ticket || ticket.Stamp.Owner != owner)
        {
            return;
        }

        IsPortBusy = true;
        PortStatusText = "正在查询…";
        try
        {
            var related = await app.ReadRelatedAsync(ticket, owner.Media, refresh: HasPort);
            if (epoch == _epoch && owner == _owner)
            {
                ApplyRelated(related);
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                PortStatusText = "查询失败";
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsPortBusy = false;
            }
        }
    }

    private void ApplyRelated(RelatedReadResult related)
    {
        if (related.View is not { } view)
        {
            PortStatusText = related.Status == ApplicationStatus.Disabled ? "需要先在设置中允许联网" : SponsorUiText.Result(new(related.Status));
            return;
        }

        // Original and target are kept distinct; nothing is aligned or merged into the current timeline here.
        if (view.Port.IsSuccess && view.Port.Value is { } port && view.Port.Status == ReadStatus.Success)
        {
            _port = port;
            HasPort = true;
            PortStatusText = "已关联 YouTube 原视频";
            PortDetailText = $"YouTube 视频：{port.YoutubeId}"
                + (port.Votes is double votes ? $" · 得票 {votes.ToString("0", CultureInfo.InvariantCulture)}" : string.Empty)
                + (port.Locked == true ? " · 已锁定" : string.Empty);
        }
        else
        {
            _port = null;
            HasPort = false;
            PortStatusText = view.Port.IsSuccess ? "这个视频还没有关联 YouTube 原视频" : "查询失败：" + SponsorUiText.Read(view.Port.Status);
            PortDetailText = string.Empty;
        }

        var external = view.Segments.IsSuccess && !view.Segments.Value.IsDefault ? view.Segments.Value.Count(s => s.Source == 2) : 0;
        PortSegmentsText = view.Segments.IsSuccess ? $"服务器上的搬运片段：{external} 个" : string.Empty;
        MappingText = view.MappingState == "PendingExplicitMapping"
            ? "搬运片段的时间尚未与本视频对齐，只有在设置中启用“使用 YouTube 搬运片段”并经过对齐确认后才会参与跳过。"
            : string.Empty;
    }

    [RelayCommand]
    private void RequestBindPort()
    {
        var id = YoutubeIdInput.Trim();
        if (!CanMutatePort || _publicId is not { } publicId || _owner is not { } owner)
        {
            Feedback(!CanMutatePort ? PortNote : "需要先在设置中创建贡献者身份");
            return;
        }

        if (id.Length != 11 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            Feedback("请输入 11 位的 YouTube 视频 ID（不是完整链接）");
            return;
        }

        Confirm("关联 YouTube 原视频？", $"将把当前视频与 YouTube 视频 {id} 关联。使用贡献者 ID {SponsorUiText.ShortId(publicId)}，发送后不能撤回。", "关联",
            () => PortCoreAsync(owner, ManualOperation.BindPort, publicId, text: id));
    }

    [RelayCommand]
    private void RequestVotePortUp() => RequestVotePort(SponsorVote.Up);

    [RelayCommand]
    private void RequestVotePortDown() => RequestVotePort(SponsorVote.Down);

    private void RequestVotePort(SponsorVote vote)
    {
        if (!CanMutatePort || _publicId is not { } publicId || _owner is not { } owner || _port is not { } port)
        {
            Feedback(!CanMutatePort ? PortNote : _port is null ? "请先查询关联信息" : "需要先在设置中创建贡献者身份");
            return;
        }

        Confirm(vote == SponsorVote.Up ? "确认关联正确？" : "确认关联有误？",
            $"对 YouTube 视频 {port.YoutubeId} 的关联投{(vote == SponsorVote.Up ? "赞同" : "反对")}票。使用贡献者 ID {SponsorUiText.ShortId(publicId)}。",
            "投票",
            () => PortCoreAsync(owner, ManualOperation.VotePort, publicId, vote: vote, port: port));
    }

    [RelayCommand]
    private void RequestUpdatePort()
    {
        if (!CanMutatePort || _owner is not { } owner || _port is not { } port)
        {
            Feedback(!CanMutatePort ? PortNote : "请先查询关联信息");
            return;
        }

        Confirm("更新搬运片段？", $"请求服务器从 YouTube 视频 {port.YoutubeId} 重新同步片段。不会自动对齐时间。", "更新",
            () => PortCoreAsync(owner, ManualOperation.UpdatePort, null, port: port));
    }

    private Task MutateCoreAsync(PlaybackOwner owner, ManualOperation operation, string? publicId, string? text = null)
        => SponsorUiWork.Track(this, MutateCoreWorkAsync(owner, operation, publicId, text));

    private async Task MutateCoreWorkAsync(PlaybackOwner owner, ManualOperation operation, string? publicId, string? text = null)
    {
        var epoch = _epoch;
        if (_app is not { } app || owner != _owner)
        {
            return;
        }

        IsBusy = true;
        try
        {
            var result = await SponsorTickets.RunAsync(app, _attachment, owner, ticket =>
                app.MutateAsync(new MutationIntent(ticket, operation, Text: text), publicId));
            if (epoch == _epoch)
            {
                Feedback(WriteText(operation, result));
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                Feedback(SponsorUiText.Operation(operation) + "没有完成，不会自动重发");
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsBusy = false;
            }
        }
    }

    private Task PortCoreAsync(PlaybackOwner owner, ManualOperation operation, string? publicId,
        string? text = null, SponsorVote vote = SponsorVote.Up, PortVideo? port = null)
        => SponsorUiWork.Track(this, PortCoreWorkAsync(owner, operation, publicId, text, vote, port));

    private async Task PortCoreWorkAsync(PlaybackOwner owner, ManualOperation operation, string? publicId,
        string? text = null, SponsorVote vote = SponsorVote.Up, PortVideo? port = null)
    {
        var epoch = _epoch;
        if (_app is not { } app || owner != _owner)
        {
            return;
        }

        IsPortBusy = true;
        try
        {
            PortMutationResult? outcome = null;
            await SponsorTickets.RunAsync(app, _attachment, owner, async ticket =>
            {
                outcome = await app.MutatePortAndRefreshAsync(new MutationIntent(ticket, operation, Vote: vote, Text: text, Port: port), publicId);
                return outcome.Mutation;
            });
            if (epoch != _epoch || owner != _owner || outcome is null)
            {
                return;
            }

            Feedback(WriteText(operation, outcome.Mutation));
            if (outcome.TargetRefresh is { } refreshed)
            {
                ApplyRelated(refreshed);
            }
        }
        catch (Exception)
        {
            if (epoch == _epoch)
            {
                Feedback(SponsorUiText.Operation(operation) + "没有完成，不会自动重发");
            }
        }
        finally
        {
            if (epoch == _epoch)
            {
                IsPortBusy = false;
            }
        }
    }

    private static string WriteText(ManualOperation operation, ApplicationResult result)
    {
        var name = SponsorUiText.Operation(operation);
        var text = result.Write?.Status switch
        {
            WriteStatus.Accepted => name + "：服务器已接受",
            WriteStatus.Rejected => name + "：服务器拒绝了此操作",
            WriteStatus.Unknown => name + "：结果未知，不会自动重发",
            WriteStatus.NotSent => name + "：" + SponsorUiText.Result(result),
            _ => name + "：" + SponsorUiText.Result(result),
        };
        return result.Status == ApplicationStatus.PersistenceFailure && result.Write?.Status is not WriteStatus.NotSent
            ? text + "（结果未能保存到本机，不会重发）"
            : text;
    }
}
