using System.Text.Json;
using System.Text.Json.Serialization;

namespace BiliCopilot.UI.SponsorBlock.Persistence;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32)]
[JsonSerializable(typeof(StateDocument))]
[JsonSerializable(typeof(BackupDocument))]
[JsonSerializable(typeof(StateDocumentV2))]
[JsonSerializable(typeof(BackupDocumentV2))]
[JsonSerializable(typeof(SponsorAuxiliarySnapshot))]
[JsonSerializable(typeof(SponsorContentPreferences))]
[JsonSerializable(typeof(PersistedContentOptions))]
[JsonSerializable(typeof(PersistedDanmakuAidOptions))]
[JsonSerializable(typeof(PersistedTextRegexLimits))]
[JsonSerializable(typeof(DurableMutationRecord))]
[JsonSerializable(typeof(IdentityDocument))]
[JsonSerializable(typeof(SponsorLocalSettings))]
internal sealed partial class PersistenceJsonContext : JsonSerializerContext { }

internal static class PersistenceJson
{
    internal const int MaximumSettingsBytes = 256 * 1024;
    internal const int MaximumLegacyDocumentBytes = 2 * 1024 * 1024;
    internal const int MaximumDocumentBytes = 4 * 1024 * 1024;
    internal const int MaximumIdentityBytes = 16 * 1024;

    // These are schema member names, not a duplicate category/action/default table.
    private static readonly string[] SettingFields = ["Policy", "Speed", "NetworkEnabled", "ViewedTelemetryEnabled",
        "IncludeExternalSegments", "IncludeDanmakuSegments", "Exclusions"];
    private static readonly string[] PolicyFields = ["Revision", "Enabled", "DisableSkipping", "IncludeMute", "IncludeFull",
        "MinDuration", "AutoSkipOnMusicVideos", "ManualSkipOnFullVideo", "WaitForChannelVerification", "SkipOnSeekToSegment",
        "SkipStrategy", "EnableAutomaticDanmaku", "UpcomingMediaSeconds", "Categories"];

    internal static bool HasFields(JsonElement element, params string[] fields) => element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Count() == fields.Length && fields.All(f => element.TryGetProperty(f, out _));

    internal static bool SettingsShape(JsonElement element) => HasFields(element, SettingFields)
        && HasFields(element.GetProperty("Policy"), PolicyFields)
        && HasFields(element.GetProperty("Speed"), "ConfiguredRate", "Fallback")
        && element.GetProperty("Exclusions").ValueKind == JsonValueKind.Array
        && element.GetProperty("Exclusions").EnumerateArray()
            .All(e => HasFields(e, "MediaKey", "Uuid", "Source", "Kind"));

    // Durable records require every field through JsonRequired. Embedded wire records have optional
    // fields: require those fields here too so removal cannot silently reinterpret persisted facts.
    internal static bool AuxiliaryShape(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        if (element.TryGetProperty("ContentPreferences", out var preferences))
        {
            if (!HasFields(element, "WhitelistedUpUids", "Filters", "Statistics", "Journal", "ContentPreferences")
                || !ContentPreferencesValidation.HasShape(preferences)) return false;
        }
        else if (!HasFields(element, "WhitelistedUpUids", "Filters", "Statistics", "Journal")) return false;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("Journal", out var journal)
            || journal.ValueKind != JsonValueKind.Array) return false;
        foreach (var entry in journal.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("Request", out var request)
                || request.ValueKind != JsonValueKind.Object
                || !OptionalShape(request, "Port", "bvID", "cid", "ytbID", "UUID", "votes", "locked")
                || !OptionalShape(request, "OriginalSegment", "segment", "cid", "UUID", "category", "actionType",
                    "locked", "hidden", "source", "videoDuration")
                || !entry.TryGetProperty("Outcome", out var outcome)) return false;
            if (outcome.ValueKind != JsonValueKind.Null && (outcome.ValueKind != JsonValueKind.Object
                || !OptionalShape(outcome, "ConfirmedPort", "bvID", "cid", "ytbID", "UUID", "votes", "locked"))) return false;
        }
        return true;
    }

    private static bool OptionalShape(JsonElement parent, string name, params string[] fields) =>
        parent.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.Null || HasFields(value, fields));

    internal static JsonDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        if (!Unique(document.RootElement)) { document.Dispose(); throw new JsonException("Duplicate property"); }
        return document;
    }

    private static bool Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !Unique(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (!Unique(item)) return false;
        return true;
    }

    internal static PersistenceStatus ValidateSettings(SponsorLocalSettings? settings)
    {
        if (settings is null || !settings.IsValid()) return PersistenceStatus.InvalidData;
        return JsonSerializer.SerializeToUtf8Bytes(settings, PersistenceJsonContext.Default.SponsorLocalSettings).Length
            <= MaximumSettingsBytes ? PersistenceStatus.Success : PersistenceStatus.TooLarge;
    }

    internal static PersistenceStatus Header(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("SchemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number)) return PersistenceStatus.Corrupt;
        return number == 1 ? PersistenceStatus.Success : PersistenceStatus.UnsupportedSchema;
    }
}
