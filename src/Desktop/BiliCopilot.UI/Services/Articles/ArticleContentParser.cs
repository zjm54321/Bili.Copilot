// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Net;
using System.Text.Json;

namespace BiliCopilot.UI.Services.Articles;

/// <summary>Bounded, inert article JSON parsing. No transport, script evaluation or UI policy.</summary>
internal static partial class ArticleContentParser
{
    internal const int MaximumHtmlCharacters = 4 * 1024 * 1024;
    internal const int MaximumJsonCharacters = 2 * 1024 * 1024;
    internal const int MaximumMetadataCharacters = 256 * 1024;
    internal const int MaximumOutputCharacters = 2 * 1024 * 1024;
    private const int MaximumDepth = 32;
    private const int MaximumNodes = 50000;
    private const int MaximumParagraphs = 4096;
    private const string StateMarker = "__INITIAL_STATE__";

    internal static LegacyArticleProjection ParseArticleViewJson(string json, string expectedArticleId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requested = ArticleRawHtmlClient.ValidateArticleId(expectedArticleId);
        try
        {
            using var document = ParseJson(json, MaximumJsonCharacters, cancellationToken);
            var root = Object(document.RootElement);
            var data = Field(root, "data");
            var remoteApiCode = Integer(Required(root, "code"));
            // Remote denial/not-found/risk codes are finite failures, not permission to retry.
            if (remoteApiCode != 0) throw Fail(remoteApiCode == -509
                ? ArticleCompatibilityFailure.RateLimited : ArticleCompatibilityFailure.RemoteApiFailure);
            var projection = ParseArticleViewData(data, expectedArticleId, requested, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return projection;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw Fail(ArticleCompatibilityFailure.MalformedData);
        }
    }

    internal static ArticleContentProjection ParseNativeHtml(string html, string expectedArticleId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requested = ArticleRawHtmlClient.ValidateArticleId(expectedArticleId);
        try
        {
            if (string.IsNullOrWhiteSpace(html)) throw Fail(ArticleCompatibilityFailure.MissingContent);
            if (html.Length > MaximumHtmlCharacters) throw Fail(ArticleCompatibilityFailure.LimitExceeded);
            // A successful HTTP status may still be Bilibili's verification page.
            // Do not mistake it for an absent article. The reader can still try its
            // fixed, independent public API fallback without executing the challenge.
            if (ArticleRawHtmlClient.IsVerificationPage(html))
                throw Fail(ArticleCompatibilityFailure.RemoteChallenge);
            var state = ExtractState(html, cancellationToken);
            using var document = ParseJson(state, MaximumJsonCharacters, cancellationToken);
            var root = Object(document.RootElement);
            var readInfo = Field(root, "readInfo");
            var hasReadInfo = readInfo.ValueKind == JsonValueKind.Object && readInfo.EnumerateObject().Any();
            if (Field(Field(root, "detail"), "modules").ValueKind == JsonValueKind.Array)
            {
                var module = ParseModules(Required(root, "detail"), expectedArticleId, requested, cancellationToken);
                if (hasReadInfo)
                    CheckMixedIdentity(Required(root, "readInfo"), requested, module.AuthorId);
                return module;
            }
            if (readInfo.ValueKind == JsonValueKind.Object && !hasReadInfo) throw Fail(ArticleCompatibilityFailure.MissingContent);
            var data = Required(root, "readInfo");
            return ParseArticleViewData(data, expectedArticleId, requested, cancellationToken);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw Fail(ArticleCompatibilityFailure.MalformedData);
        }
    }

    private static LegacyArticleProjection ParseArticleViewData(JsonElement data, string originalId, string requested,
        CancellationToken cancellationToken)
    {
        if (Identifier(Required(data, "id")) != requested) throw Fail(ArticleCompatibilityFailure.IdentityMismatch);
        var type = Integer(Required(data, "type"));
        if (type is not (0 or 3 or 4)
            && Field(Field(Field(data, "opus"), "content"), "paragraphs").ValueKind != JsonValueKind.Array)
            throw Fail(ArticleCompatibilityFailure.UnsupportedFormat);
        // dyn_id_str/opus_id identify a different resource and never replace data.id.
        return ParseLegacy(data, originalId, cancellationToken, type);
    }

    internal static ArticleReadMetadata ParseMetadata(string json, string expectedAuthorId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var authorId = Identifier(expectedAuthorId);
        try
        {
            using var document = ParseJson(json, MaximumMetadataCharacters, cancellationToken);
            var root = Object(document.RootElement);
            var remoteApiCode = Integer(Required(root, "code"));
            if (remoteApiCode != 0) throw Fail(ArticleCompatibilityFailure.InvalidMetadata);
            var data = Object(Required(root, "data"));
            var mid = Identifier(Required(data, "mid"));
            if (mid != authorId) throw Fail(ArticleCompatibilityFailure.IdentityMismatch);
            var like = Integer(Required(data, "like"));
            if (like is not (0 or 1)) throw Fail(ArticleCompatibilityFailure.InvalidMetadata);
            var favorite = Boolean(Required(data, "favorite"));
            var counts = new ArticleCounters();
            if (data.TryGetProperty("stats", out var stats) && stats.ValueKind != JsonValueKind.Null)
            {
                Object(stats);
                counts = new(Counter(stats, "view"), Counter(stats, "favorite"), Counter(stats, "like"),
                    Counter(stats, "reply"), Counter(stats, "share"), Counter(stats, "coin"));
            }
            return new(mid, like == 1, favorite, counts);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            throw Fail(ArticleCompatibilityFailure.InvalidMetadata);
        }
    }

    private static LegacyArticleProjection ParseLegacy(JsonElement readInfo, string originalId,
        CancellationToken cancellationToken, int articleViewType)
    {
        var author = Field(readInfo, "author");
        if (author.ValueKind != JsonValueKind.Object) throw Fail(ArticleCompatibilityFailure.MissingAuthor);
        var authorId = Identifier(Required(author, "mid", ArticleCompatibilityFailure.MissingAuthor));
        var name = Text(Required(author, "name", ArticleCompatibilityFailure.MissingAuthor), ArticleCompatibilityFailure.MissingAuthor);
        var publication = Publication(Field(readInfo, "publish_time"));
        string? avatar = null;
        if (author.TryGetProperty("face", out var face) && face.ValueKind != JsonValueKind.Null)
            avatar = HttpUrl(Text(face));
        string body;
        var content = Field(readInfo, "content");
        var opusParagraphs = Field(Field(Field(readInfo, "opus"), "content"), "paragraphs");
        if (opusParagraphs.ValueKind == JsonValueKind.Array && opusParagraphs.GetArrayLength() != 0)
            body = RenderOpusParagraphs(opusParagraphs, cancellationToken);
        else if (articleViewType is 3 or 4)
        {
            // Migrated type 3/4 content is Quill JSON/plain text, not legacy HTML.
            // The structured opus body preserves pictures and paragraph boundaries.
            var paragraphs = Field(Field(Field(readInfo, "opus"), "content"), "paragraphs");
            if (paragraphs.ValueKind != JsonValueKind.Array) throw Fail(ArticleCompatibilityFailure.UnsupportedFormat);
            body = RenderOpusParagraphs(paragraphs, cancellationToken);
        }
        else if (content.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(content.GetString()))
            body = BoundedLegacyArticleHtml.Render(content.GetString()!, cancellationToken);
        else
        {
            if (articleViewType == 0) throw Fail(ArticleCompatibilityFailure.MissingContent);
            if (content.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
                throw Fail(ArticleCompatibilityFailure.MalformedData);
            var paragraphs = Field(Field(Field(readInfo, "opus"), "content"), "paragraphs");
            if (paragraphs.ValueKind != JsonValueKind.Array) throw Fail(ArticleCompatibilityFailure.MissingContent);
            body = RenderOpusParagraphs(paragraphs, cancellationToken);
        }
        return new(originalId, authorId, name, publication, body, avatar, new ArticleCounters());
    }

    private static void CheckMixedIdentity(JsonElement legacy, string requested, string authorId)
    {
        if (Identifier(Field(legacy, "id")) != requested) throw Fail(ArticleCompatibilityFailure.IdentityMismatch);
        var author = Field(legacy, "author");
        if (author.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return;
        Object(author);
        if (author.EnumerateObject().Any() && Identifier(Field(author, "mid")) != authorId)
            throw Fail(ArticleCompatibilityFailure.IdentityMismatch);
    }

    private static JsonElement Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var field) ? field : default;

    private static ModuleArticleProjection ParseModules(JsonElement detail, string originalId, string requested,
        CancellationToken cancellationToken)
    {
        var basic = Object(Required(detail, "basic"));
        if (Integer(Required(basic, "comment_type")) != 12) throw Fail(ArticleCompatibilityFailure.IdentityMismatch);
        var hasArticleId = false;
        foreach (var key in new[] { "comment_id_str", "rid_str" })
        {
            if (!basic.TryGetProperty(key, out var value)) continue;
            if (Identifier(value) != requested) throw Fail(ArticleCompatibilityFailure.IdentityMismatch);
            hasArticleId = true;
        }
        if (!hasArticleId) throw Fail(ArticleCompatibilityFailure.IdentityMismatch);
        // detail.id_str is an opus identity, never the article/comment identity.
        var modules = Array(Required(detail, "modules"));
        if (modules.GetArrayLength() is 0 or > 64) throw Fail(ArticleCompatibilityFailure.LimitExceeded);
        JsonElement? author = null;
        var bodyModules = new List<(bool IsTop, JsonElement Payload)>();
        string? title = null;
        var moduleTypes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var moduleValue in modules.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var module = Object(moduleValue);
            var type = Text(Required(module, "module_type"));
            if (!moduleTypes.Add(type)) throw Fail(ArticleCompatibilityFailure.MalformedData);
            switch (type)
            {
                case "MODULE_TYPE_AUTHOR": author = Object(Required(module, "module_author")); break;
                case "MODULE_TYPE_CONTENT": bodyModules.Add((false, Object(Required(module, "module_content")))); break;
                case "MODULE_TYPE_TOP": bodyModules.Add((true, Object(Required(module, "module_top")))); break;
                case "MODULE_TYPE_TITLE": title = Text(Required(Object(Required(module, "module_title")), "text")); break;
                case "MODULE_TYPE_STAT": Object(Required(module, "module_stat")); break; // Use real viewinfo counts, not formatted labels.
                // Observed opus footer metadata: tags, copyright label and share summary,
                // not body content. Unknown/restricted-content modules still fail closed.
                case "MODULE_TYPE_EXTEND": Object(Required(module, "module_extend")); break;
                case "MODULE_TYPE_COPYRIGHT": Object(Required(module, "module_copyright")); break;
                case "MODULE_TYPE_BOTTOM": Object(Required(module, "module_bottom")); break;
                case "MODULE_TYPE_COLLECTION": Object(Required(module, "module_collection")); break;
                case "MODULE_TYPE_TOPIC": Object(Required(module, "module_topic")); break;
                default: throw Fail(ArticleCompatibilityFailure.UnsupportedModule);
            }
        }
        if (author is not { } authorValue) throw Fail(ArticleCompatibilityFailure.MissingAuthor);
        if (bodyModules.Count == 0) throw Fail(ArticleCompatibilityFailure.MissingContent);
        var authorId = Identifier(Required(authorValue, "mid"));
        if (basic.TryGetProperty("uid", out var uid) && Identifier(uid) != authorId)
            throw Fail(ArticleCompatibilityFailure.IdentityMismatch);
        var name = Text(Required(authorValue, "name"), ArticleCompatibilityFailure.MissingAuthor);
        var publication = Publication(Required(authorValue, "pub_ts"));
        title ??= Text(Required(basic, "title"));
        string? avatar = null;
        if (authorValue.TryGetProperty("face", out var face) && face.ValueKind != JsonValueKind.Null)
            avatar = HttpUrl(Text(face));
        var body = RenderOpusModules(bodyModules, cancellationToken);
        // Tags are intentionally not projected as an empty list. Their new-envelope mapping is unproved.
        return new(originalId, authorId, name, publication, title, body, avatar, new ArticleCounters());
    }

    private sealed class BodyWriter(CancellationToken cancellationToken)
    {
        private readonly StringBuilder _output = new();
        private int _nodes;
        internal bool Meaningful { get; set; }
        internal void Visit()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++_nodes > 16000) throw Fail(ArticleCompatibilityFailure.LimitExceeded);
        }
        internal void Add(string text) => Append(_output, text);
        internal (int Length, bool Meaningful) Mark() => (_output.Length, Meaningful);
        internal void Restore((int Length, bool Meaningful) mark)
        {
            _output.Length = mark.Length;
            Meaningful = mark.Meaningful;
        }
        internal string Complete() => Meaningful ? _output.ToString() : throw Fail(ArticleCompatibilityFailure.MissingContent);
    }

    private static void RenderWords(string words, BodyWriter writer)
    {
        writer.Add(WebUtility.HtmlEncode(words).Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal).Replace("\n", "<br />", StringComparison.Ordinal));
        writer.Meaningful |= !string.IsNullOrWhiteSpace(words);
    }

    private static void RenderPicture(JsonElement picture, BodyWriter writer, bool meaningful)
    {
        writer.Visit();
        var url = HttpUrl(Text(Required(picture, "url")));
        writer.Add("<p><img src=\"" + WebUtility.HtmlEncode(url) + "\" alt=\"\" /></p>");
        writer.Meaningful |= meaningful;
    }

    private static string ExtractState(string html, CancellationToken cancellationToken)
    {
        var search = 0;
        string? json = null;
        while (search < html.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var marker = html.IndexOf(StateMarker, search, StringComparison.Ordinal);
            if (marker < 0) break;
            var position = marker + StateMarker.Length;
            search = position;
            while (position < html.Length && char.IsWhiteSpace(html[position])) position++;
            if (position >= html.Length || html[position] != '=') continue;
            position++;
            while (position < html.Length && char.IsWhiteSpace(html[position])) position++;
            if (position >= html.Length || html[position] != '{') throw Fail(ArticleCompatibilityFailure.UnsupportedFormat);
            if (json is not null) throw Fail(ArticleCompatibilityFailure.UnsupportedFormat);
            var start = position;
            var bytes = Encoding.UTF8.GetBytes(html.Substring(start, Math.Min(MaximumJsonCharacters, html.Length - start)));
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = MaximumDepth });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                throw Fail(ArticleCompatibilityFailure.MalformedData);
            // Parse exactly one JSON value, not the following JavaScript or script markup.
            using var document = JsonDocument.ParseValue(ref reader);
            cancellationToken.ThrowIfCancellationRequested();
            json = document.RootElement.GetRawText();
            position = start + Encoding.UTF8.GetCharCount(bytes.AsSpan(0, (int)reader.BytesConsumed));
            while (position < html.Length && char.IsWhiteSpace(html[position])) position++;
            if (position >= html.Length || html[position] != ';'
                && !html.AsSpan(position).StartsWith("</script", StringComparison.OrdinalIgnoreCase))
                throw Fail(ArticleCompatibilityFailure.UnsupportedFormat);
            ValidateScriptEnvelope(html, marker, position);
            search = position + 1;
        }
        return json ?? throw Fail(ArticleCompatibilityFailure.UnsupportedFormat);
    }

    private static void ValidateScriptEnvelope(string html, int marker, int assignmentEnd)
    {
        var opening = html.LastIndexOf("<script", marker, StringComparison.OrdinalIgnoreCase);
        var priorClose = html.LastIndexOf("</script", marker, StringComparison.OrdinalIgnoreCase);
        if (opening < 0 || opening <= priorClose) throw Fail(ArticleCompatibilityFailure.UnsupportedFormat);
        var commentStart = html.LastIndexOf("<!--", opening, StringComparison.Ordinal);
        var commentEnd = html.LastIndexOf("-->", opening, StringComparison.Ordinal);
        if (commentStart > commentEnd) throw Fail(ArticleCompatibilityFailure.UnsupportedFormat);
        var position = opening + 7;
        if (position >= marker || (html[position] != '>' && !char.IsWhiteSpace(html[position])))
            throw Fail(ArticleCompatibilityFailure.UnsupportedFormat);
        var quote = '\0';
        for (; position < marker; position++)
        {
            var character = html[position];
            if (quote != '\0') { if (character == quote) quote = '\0'; }
            else if (character is '\'' or '"') quote = character;
            else if (character == '>') break;
        }
        if (position >= marker) throw Fail(ArticleCompatibilityFailure.UnsupportedFormat);
        var prefix = html.AsSpan(position + 1, marker - position - 1).Trim().ToString();
        if (prefix is not ("" or "window." or "var" or "let" or "const"))
            throw Fail(ArticleCompatibilityFailure.UnsupportedFormat);
        var close = html.IndexOf("</script", position + 1, StringComparison.OrdinalIgnoreCase);
        if (close < assignmentEnd || close + 8 >= html.Length
            || (html[close + 8] != '>' && !char.IsWhiteSpace(html[close + 8])))
            throw Fail(ArticleCompatibilityFailure.UnsupportedFormat);
    }

    private static JsonDocument ParseJson(string json, int limit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(json)) throw Fail(ArticleCompatibilityFailure.MalformedData);
        if (json.Length > limit) throw Fail(ArticleCompatibilityFailure.LimitExceeded);
        var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = MaximumDepth });
        try
        {
            var remaining = MaximumNodes;
            CheckDocument(document.RootElement, ref remaining, cancellationToken);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    private static void CheckDocument(JsonElement element, ref int remaining, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (--remaining < 0) throw Fail(ArticleCompatibilityFailure.LimitExceeded);
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Fail(ArticleCompatibilityFailure.MalformedData);
                CheckDocument(property.Value, ref remaining, cancellationToken);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckDocument(child, ref remaining, cancellationToken);
    }

    private static void Append(StringBuilder builder, string text)
    {
        if (text.Length > MaximumOutputCharacters - builder.Length) throw Fail(ArticleCompatibilityFailure.LimitExceeded);
        builder.Append(text);
    }

    internal static string HttpUrl(string value)
    {
        foreach (var character in value)
            if (char.IsControl(character)) throw Fail(ArticleCompatibilityFailure.UnsafeUrl);
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) throw Fail(ArticleCompatibilityFailure.UnsafeUrl);
        return uri.AbsoluteUri;
    }

    private static string Identifier(JsonElement element) => Identifier(element.ValueKind == JsonValueKind.String
        ? element.GetString()! : element.ValueKind == JsonValueKind.Number ? element.GetRawText() : string.Empty);
    private static string Identifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 20
            || !ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number == 0)
            throw Fail(ArticleCompatibilityFailure.IdentityMismatch);
        return number.ToString(CultureInfo.InvariantCulture);
    }
    private static DateTimeOffset Publication(JsonElement element)
    {
        // Opus module_author.pub_ts is a decimal string; legacy publish_time is a number.
        long timestamp = 0;
        var valid = element.ValueKind == JsonValueKind.String
            ? long.TryParse(element.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out timestamp)
            : element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out timestamp);
        if (!valid || timestamp <= 0)
            throw Fail(ArticleCompatibilityFailure.MissingPublication);
        try { return DateTimeOffset.FromUnixTimeSeconds(timestamp); }
        catch (ArgumentOutOfRangeException) { throw Fail(ArticleCompatibilityFailure.MissingPublication); }
    }
    private static int? Counter(JsonElement stats, string name)
    {
        if (!stats.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        var count = Integer(value);
        return count >= 0 ? count : throw Fail(ArticleCompatibilityFailure.InvalidMetadata);
    }
    private static JsonElement Object(JsonElement value) => value.ValueKind == JsonValueKind.Object ? value : throw Fail(ArticleCompatibilityFailure.MalformedData);
    private static JsonElement Array(JsonElement value) => value.ValueKind == JsonValueKind.Array ? value : throw Fail(ArticleCompatibilityFailure.MalformedData);
    private static JsonElement Required(JsonElement value, string name,
        ArticleCompatibilityFailure failure = ArticleCompatibilityFailure.MalformedData) => Object(value).TryGetProperty(name, out var property)
        ? property : throw Fail(failure);
    private static string String(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Fail(ArticleCompatibilityFailure.MalformedData);
    private static string Text(JsonElement value, ArticleCompatibilityFailure failure = ArticleCompatibilityFailure.MalformedData)
    {
        var text = String(value);
        return !string.IsNullOrWhiteSpace(text) ? text : throw Fail(failure);
    }
    private static int Integer(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
        ? number : throw Fail(ArticleCompatibilityFailure.MalformedData);
    private static bool Boolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean() : throw Fail(ArticleCompatibilityFailure.MalformedData);
    private static ArticleCompatibilityException Fail(ArticleCompatibilityFailure failure) => new(failure);
}
