using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace BiliCopilot.UI.SponsorBlock.Models;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 32)]
[JsonSerializable(typeof(SponsorSegment))]
[JsonSerializable(typeof(SponsorSegment[]))]
[JsonSerializable(typeof(SegmentBucket[]))]
[JsonSerializable(typeof(LockBucket[]))]
[JsonSerializable(typeof(PortVideo))]
[JsonSerializable(typeof(PortVideo[]))]
[JsonSerializable(typeof(SponsorUserInfo))]
[JsonSerializable(typeof(SubmitPayload))]
[JsonSerializable(typeof(PortBindPayload))]
[JsonSerializable(typeof(PortVotePayload))]
[JsonSerializable(typeof(PortUpdatePayload))]
[JsonSerializable(typeof(SegmentDraft))]
[JsonSerializable(typeof(SegmentDraft[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int))]
internal partial class SponsorJsonContext : JsonSerializerContext;
