#nullable enable

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text.Json;

namespace BiliCopilot.UI.Live;

internal static class LiveProtocol
{
    internal const int MaxMessageBytes = 1024 * 1024;
    internal const int MaxInflatedBytes = 4 * 1024 * 1024;
    internal const int MaxDepth = 3;
    internal const int MaxPackets = 4096;
    internal const int MaxTextLength = 4096;

    internal static byte[] Encode(uint operation, ReadOnlySpan<byte> body)
    {
        if (body.Length > MaxMessageBytes - 16)
            throw Invalid();
        var packet = new byte[16 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(0, 4), (uint)packet.Length);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4, 2), 16);
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(6, 2), 1);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8, 4), operation);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12, 4), 1);
        body.CopyTo(packet.AsSpan(16));
        return packet;
    }

    internal static byte[] Authentication(long uid, long roomId, string key, string buvid)
    {
        if (uid < 0 || roomId <= 0 || string.IsNullOrEmpty(key) || key.Length > 4096
            || string.IsNullOrEmpty(buvid) || buvid.Length > 512)
            throw Invalid();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("uid", uid);
            writer.WriteNumber("roomid", roomId);
            writer.WriteNumber("protover", 3);
            writer.WriteString("platform", "web");
            writer.WriteNumber("type", 2);
            writer.WriteString("key", key);
            writer.WriteString("buvid", buvid);
            writer.WriteEndObject();
        }
        return Encode(7, stream.ToArray());
    }

    // One budget spans every sibling and nested compressed packet in this WS message.
    internal static void Decode(ReadOnlyMemory<byte> message, Action<uint, ReadOnlyMemory<byte>> packet)
    {
        if (message.Length == 0 || message.Length > MaxMessageBytes)
            throw Invalid();
        DecodePackets(message, 0, new Budget(), packet);
    }

    private static void DecodePackets(ReadOnlyMemory<byte> data, int depth, Budget budget,
        Action<uint, ReadOnlyMemory<byte>> packet)
    {
        var offset = 0;
        while (offset < data.Length)
        {
            if (++budget.Packets > MaxPackets || data.Length - offset < 16)
                throw Invalid();
            var header = data.Span.Slice(offset, 16);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var headerLength = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(4));
            var version = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(6));
            var operation = BinaryPrimitives.ReadUInt32BigEndian(header.Slice(8));
            if (headerLength < 16 || length < headerLength || length > (uint)(data.Length - offset))
                throw Invalid();
            var body = data.Slice(offset + headerLength, (int)length - headerLength);
            if (version is 0 or 1)
                packet(operation, body);
            else if (version is 2 or 3)
            {
                if (depth >= MaxDepth || body.IsEmpty)
                    throw Invalid();
                using var input = new MemoryStream(body.ToArray(), false);
                using Stream inflater = version == 2
                    ? new ZLibStream(input, CompressionMode.Decompress)
                    : new BrotliStream(input, CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                try
                {
                    while (true)
                    {
                        var remaining = MaxInflatedBytes - budget.Inflated;
                        var count = inflater.Read(buffer, 0, Math.Min(buffer.Length, remaining + 1));
                        if (count == 0)
                            break;
                        if (count > remaining)
                            throw Invalid();
                        budget.Inflated += count;
                        output.Write(buffer, 0, count);
                    }
                }
                catch (IOException) { throw Invalid(); }
                if (output.Length == 0)
                    throw Invalid();
                DecodePackets(output.ToArray(), depth + 1, budget, packet);
            }
            else
                throw Invalid();
            offset += (int)length;
        }
    }

    internal static void RequireAuthenticationSuccess(ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty || body.Length > 16384)
            throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(body, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Invalid();
            int? code = null;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name != "code")
                    continue;
                if (code.HasValue || property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var parsed))
                    throw Invalid();
                code = parsed;
            }
            if (!code.HasValue)
                throw Invalid();
            var value = code.Value;
            LiveChatDiagnostics.Write("Authentication", "Acknowledgement received", value);
            if (value != 0)
                throw new LiveChatFailure("Live chat authentication was denied.", false, true);
        }
        catch (JsonException) { throw Invalid(); }
    }

    internal static LiveChatMessage? ReadMessage(LiveBinding binding, ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty || body.Length > 65536)
            return null;
        try
        {
            using var document = JsonDocument.Parse(body, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cmd", out var cmd)
                || cmd.ValueKind != JsonValueKind.String)
                return null;
            var command = cmd.GetString();
            if (command is "SUPER_CHAT_MESSAGE" or "SUPER_CHAT_MESSAGE_JPN")
                return ReadSuperChat(binding, root);
            if (command != "DANMU_MSG" && !(command?.StartsWith("DANMU_MSG:", StringComparison.Ordinal) ?? false))
                return null;
            if (!root.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Array
                || info.GetArrayLength() < 3 || info[1].ValueKind != JsonValueKind.String
                || info[2].ValueKind != JsonValueKind.Array || info[2].GetArrayLength() < 2)
                return null;
            var user = info[2];
            if (user[0].ValueKind != JsonValueKind.Number || !user[0].TryGetInt64(out var uid) || uid < 0
                || user[1].ValueKind != JsonValueKind.String)
                return null;
            var text = info[1].GetString();
            var name = user[1].GetString();
            if (string.IsNullOrEmpty(text) || text.Length > MaxTextLength || name is null || name.Length > 128)
                return null;
            // The researched rnd field is not a reliable identity. Preserve repeats rather
            // than silently conflating identical messages. No unverified ID field is guessed.
            return new LiveChatMessage(binding, null, uid, name, text, DateTimeOffset.UtcNow);
        }
        catch (JsonException) { return null; }
    }

    private static LiveChatMessage? ReadSuperChat(LiveBinding binding, JsonElement root)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("message", out var text) || text.ValueKind != JsonValueKind.String
            || !data.TryGetProperty("uid", out var userId) || userId.ValueKind != JsonValueKind.Number
            || !userId.TryGetInt64(out var uid) || uid < 0
            || !data.TryGetProperty("user_info", out var user) || user.ValueKind != JsonValueKind.Object
            || !user.TryGetProperty("uname", out var name) || name.ValueKind != JsonValueKind.String)
            return null;
        var message = text.GetString();
        var userName = name.GetString();
        if (string.IsNullOrEmpty(message) || message.Length > MaxTextLength || userName is null || userName.Length > 128)
            return null;
        // The translated duplicate shares the original SC id. No price, rank or gift metadata is retained.
        string? eventId = null;
        if (data.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String)
        {
            var value = id.ToString();
            if (value.Length is > 0 and <= 120) eventId = "sc:" + value;
        }
        return new LiveChatMessage(binding, eventId, uid, userName, message, DateTimeOffset.UtcNow);
    }

    internal static LiveAudienceUpdate? ReadAudience(LiveBinding binding, ReadOnlyMemory<byte> body)
    {
        if (body.IsEmpty || body.Length > 65536) return null;
        try
        {
            using var document = JsonDocument.Parse(body, JsonOptions);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cmd", out var cmd)
                || cmd.ValueKind != JsonValueKind.String) return null;
            var command = cmd.GetString();
            if (command is not ("WATCHED_CHANGE" or "ONLINE_RANK_COUNT")) return null;
            var metric = command == "WATCHED_CHANGE" ? LiveAudienceMetric.Watched : LiveAudienceMetric.OnlineRank;
            var field = metric == LiveAudienceMetric.Watched ? "num" : "count";
            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty(field, out var count) || count.ValueKind != JsonValueKind.Number
                || !count.TryGetInt64(out var value) || value < 0) return null;
            return new LiveAudienceUpdate(binding, value, metric);
        }
        catch (JsonException) { return null; }
    }

    internal static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32 };
    internal static LiveChatFailure Invalid() => new("Live chat protocol is unavailable.", false);

    private sealed class Budget
    {
        internal int Inflated;
        internal int Packets;
    }
}

// Supports explicit usable identities without treating rnd or text as an ID.
// Current observed DANMU_MSG variants intentionally produce a null EventId.
internal sealed class LiveEventDeduplicator
{
    private readonly HashSet<(long User, string Id)> _seen = new();
    private readonly Queue<(long User, string Id)> _order = new();

    internal bool Admit(LiveChatMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.EventId) || message.EventId.Length > 128)
            return true;
        var key = (message.UserId, message.EventId);
        if (!_seen.Add(key))
            return false;
        _order.Enqueue(key);
        if (_order.Count > 512)
            _seen.Remove(_order.Dequeue());
        return true;
    }
}

internal sealed class LiveChatFailure : Exception
{
    public LiveChatFailure() : this("Live chat is unavailable.", false) { }

    public LiveChatFailure(string? safeMessage) : this(safeMessage ?? "Live chat is unavailable.", false) { }

    // Only already-sanitized local causes belong here. Transport/SDK catches must still classify
    // to fixed messages and must never forward their raw exception through this constructor.
    public LiveChatFailure(string? safeMessage, Exception? innerException)
        : base(safeMessage ?? "Live chat is unavailable.", innerException)
    {
        Transient = false;
        Unavailable = false;
        Closed = false;
    }

    internal LiveChatFailure(string safeMessage, bool transient, bool unavailable = false, bool closed = false) : base(safeMessage)
    {
        Transient = transient;
        Unavailable = unavailable;
        Closed = closed;
    }

    internal bool Transient { get; }
    internal bool Unavailable { get; }
    internal bool Closed { get; }
}

// Bounded owner-local observations: no text, identifiers, exception objects, logging callbacks,
// or UI publications. Recording a cleanup/observer fault cannot itself interrupt the drain.
[Flags]
internal enum LiveFailureCategory
{
    None = 0,
    CloseHandshake = 1 << 0,
    SocketAbort = 1 << 1,
    OwnedTaskFault = 1 << 2,
    CancellationCallback = 1 << 3,
    CancellationAfterRelease = 1 << 4,
    Dispatcher = 1 << 5,
    StatusSubscriber = 1 << 6,
    MessageSubscriber = 1 << 7,
    AccountResolver = 1 << 8,
    RegistrationRelease = 1 << 9,
    CollectionSubscriber = 1 << 10,
    PropertySubscriber = 1 << 11,
    ChatRelease = 1 << 12,
    BackendRelease = 1 << 13,
    ReportSubscriber = 1 << 14,
}
