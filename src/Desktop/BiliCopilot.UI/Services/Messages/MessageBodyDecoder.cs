// Copyright (c) Bili Copilot. All rights reserved.

using System.Text.Json;

namespace BiliCopilot.UI.Services.Messages;

/// <summary>Projects one supported web-text envelope; unsupported content stays verbatim.</summary>
internal static class MessageBodyDecoder
{
    internal static string? Decode(string? content)
    {
        if (string.IsNullOrEmpty(content)) return content;
        var text = content.AsSpan().TrimStart();
        if (text.IsEmpty || text[0] != '{') return content;

        // Keep ill-formed UTF-16 verbatim rather than allowing an encoder to replace it.
        for (var i = 0; i < content.Length; i++)
        {
            if (!char.IsSurrogate(content[i])) continue;
            if (!char.IsHighSurrogate(content[i]) || ++i >= content.Length || !char.IsLowSurrogate(content[i])) return content;
        }

        try
        {
            // DOM access is AOT-safe. Bound nesting without truncating or rewriting fallback text.
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("web", out var web) && web.ValueKind == JsonValueKind.String
                ? web.GetString() : content; // Decode JSON escapes once, never HTML/URL escapes or nested envelopes.
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            // Malformed JSON/Unicode or an unsupported string representation must not lose the original body.
            return content;
        }
    }
}
