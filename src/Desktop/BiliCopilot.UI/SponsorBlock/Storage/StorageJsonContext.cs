using System.Text.Json.Serialization;
using BiliCopilot.UI.SponsorBlock.Models;

namespace BiliCopilot.UI.SponsorBlock.Storage;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 32)]
[JsonSerializable(typeof(QueueDocument))]
[JsonSerializable(typeof(SegmentDraft))]
internal sealed partial class StorageJsonContext : JsonSerializerContext { }
