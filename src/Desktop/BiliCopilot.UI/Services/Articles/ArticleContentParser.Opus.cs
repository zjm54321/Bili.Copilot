// Copyright (c) Bili Copilot. All rights reserved.

using System.Globalization;
using System.Net;
using System.Text.Json;

namespace BiliCopilot.UI.Services.Articles;

internal static partial class ArticleContentParser
{
    private static string RenderOpusParagraphs(JsonElement paragraphs, string articleId, string path,
        CancellationToken token, Action<ArticleParseNotice>? notice)
    {
        var writer = new BodyWriter(token);
        RenderParagraphs(paragraphs, writer, articleId, path, notice);
        return CompleteBody(writer, articleId, path);
    }

    private static string RenderOpusModules(IReadOnlyList<(bool IsTop, JsonElement Payload, int Index)> modules,
        string articleId, CancellationToken token, Action<ArticleParseNotice>? notice)
    {
        var writer = new BodyWriter(token);
        for (var index = 0; index < modules.Count; index++)
        {
            var module = modules[index];
            var path = "$.detail.modules[" + module.Index.ToString(CultureInfo.InvariantCulture) + "]";
            if (module.IsTop)
                RecoverContent(module.Payload, writer, articleId, path + ".module_top", "MODULE_TYPE_TOP", true, notice,
                    () => RenderOpusPictures(Array(Required(Required(Required(module.Payload, "display"), "album"), "pics")),
                        writer, articleId, path + ".module_top.display.album.pics", notice));
            else
                RenderParagraphs(Array(Required(module.Payload, "paragraphs")), writer, articleId,
                    path + ".module_content.paragraphs", notice);
        }
        return CompleteBody(writer, articleId, "$.detail.modules");
    }

    private static string CompleteBody(BodyWriter writer, string articleId, string path)
    {
        try { return writer.Complete(); }
        catch (ArticleCompatibilityException ex) { ex.SetContext(articleId, path); throw; }
    }

    private static void RenderParagraphs(JsonElement paragraphs, BodyWriter writer, string articleId,
        string path, Action<ArticleParseNotice>? notice)
    {
        if (paragraphs.GetArrayLength() > MaximumParagraphs) throw Fail(ArticleCompatibilityFailure.LimitExceeded);
        var index = 0;
        foreach (var paragraph in paragraphs.EnumerateArray())
        {
            var itemPath = path + "[" + (index++).ToString(CultureInfo.InvariantCulture) + "]";
            RecoverContent(paragraph, writer, articleId, itemPath, "para_type=" + TypeLabel(Field(paragraph, "para_type")),
                true, notice, () => RenderParagraph(paragraph, writer, articleId, itemPath, notice));
        }
    }

    private static void RenderParagraph(JsonElement paragraph, BodyWriter writer, string articleId,
        string path, Action<ArticleParseNotice>? notice)
    {
        switch (Integer(Required(paragraph, "para_type")))
        {
            case 1:
                var paragraphText = Field(paragraph, "text");
                // The API serializes intentional blank lines as text:{} (without nodes).
                // Preserve the break, but do not count blank-only content as a body.
                if (paragraphText.ValueKind == JsonValueKind.Object && !paragraphText.EnumerateObject().Any())
                {
                    writer.Add("<p><br /></p>");
                    break;
                }
                writer.Add("<p>");
                RenderOpusNodes(Array(Required(Required(paragraph, "text"), "nodes")), writer, articleId, path + ".text.nodes", notice);
                writer.Add("</p>");
                break;
            case 2:
                RenderOpusPictures(Array(Required(Required(paragraph, "pic"), "pics")), writer, articleId, path + ".pic.pics", notice);
                break;
            case 3:
                // Both an image-backed divider and an ordinary divider have the same inert semantics.
                writer.Add("<hr />");
                writer.Meaningful = true;
                break;
            case 4:
                writer.Add("<blockquote><p>");
                var quote = Field(paragraph, "blockquote");
                var text = Field(paragraph, "text");
                var textPath = path + ".text.nodes";
                if (Field(text, "nodes").ValueKind != JsonValueKind.Array)
                {
                    text = Field(quote, "text");
                    textPath = path + ".blockquote.text.nodes";
                }
                RenderOpusNodes(Array(Required(text, "nodes")), writer, articleId, textPath, notice);
                writer.Add("</p></blockquote>");
                break;
            case 5:
                RenderOpusList(Required(paragraph, "list"), writer, articleId, path + ".list", notice);
                break;
            case 6:
                // The article/view numeric schema uses 6 for a formatted list item;
                // the page module schema uses 6 for a link card. Inspect the payload, not just its number.
                var format = Field(Field(paragraph, "format"), "list_format");
                if (format.ValueKind == JsonValueKind.Object)
                {
                    var order = Field(format, "order");
                    var ordered = order.ValueKind == JsonValueKind.Number && order.TryGetInt32(out var number) && number > 0;
                    writer.Add(ordered ? "<ol start=\"" + order.GetRawText() + "\"><li>" : "<ul><li>");
                    RenderOpusNodes(Array(Required(Required(paragraph, "text"), "nodes")), writer, articleId, path + ".text.nodes", notice);
                    writer.Add(ordered ? "</li></ol>" : "</li></ul>");
                }
                else RenderOpusCard(Required(Required(paragraph, "link_card"), "card"), writer);
                break;
            case 7:
                var code = Required(paragraph, "code");
                var codeText = WebUtility.HtmlDecode(String(Required(code, "content")));
                writer.Add("<pre><code>");
                writer.Add(WebUtility.HtmlEncode(codeText));
                writer.Add("</code></pre>");
                writer.Meaningful |= !string.IsNullOrWhiteSpace(codeText);
                break;
            case 8:
            case 9:
                // The native numeric schema uses 9 for headings, the page module
                // schema uses 8. Select by payload so votes/future types still degrade safely.
                var heading = Field(paragraph, "heading");
                if (heading.ValueKind == JsonValueKind.Object)
                {
                    var levelValue = Field(heading, "level");
                    var level = levelValue.ValueKind == JsonValueKind.Number && levelValue.TryGetInt32(out var headingLevel)
                        && headingLevel is >= 1 and <= 6 ? headingLevel : 2;
                    var tag = "h" + level.ToString(CultureInfo.InvariantCulture);
                    writer.Add("<" + tag + ">");
                    RenderOpusNodes(Array(Required(heading, "nodes")), writer, articleId, path + ".heading.nodes", notice);
                    writer.Add("</" + tag + ">");
                    break;
                }
                throw Fail(ArticleCompatibilityFailure.UnsupportedParagraph);
            // Votes/unknown future payloads are intentionally not interactive. RecoverContent
            // keeps their title/options/text, or an explicit placeholder if no text is available.
            default: throw Fail(ArticleCompatibilityFailure.UnsupportedParagraph);
        }
    }

    private static void RenderOpusNodes(JsonElement nodes, BodyWriter writer, string articleId,
        string path, Action<ArticleParseNotice>? notice)
    {
        var index = 0;
        foreach (var node in nodes.EnumerateArray())
        {
            var itemPath = path + "[" + (index++).ToString(CultureInfo.InvariantCulture) + "]";
            var type = Field(node, "type");
            if (type.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) type = Field(node, "node_type");
            var label = "node_type=" + TypeLabel(type);
            var richType = Field(Field(node, "rich"), "type");
            if (richType.ValueKind != JsonValueKind.Undefined) label += "/rich.type=" + TypeLabel(richType);
            RecoverContent(node, writer, articleId, itemPath, label, false, notice,
                () => RenderOpusNode(node, writer));
        }
    }

    private static void RenderOpusNode(JsonElement node, BodyWriter writer)
    {
        var word = Field(node, "word");
        if (word.ValueKind == JsonValueKind.Object)
        {
            // Unknown presentation metadata must not invalidate otherwise valid text.
            var style = Field(word, "style");
            var bold = Field(style, "bold").ValueKind == JsonValueKind.True;
            var italic = Field(style, "italic").ValueKind == JsonValueKind.True;
            var strike = Field(style, "strikethrough").ValueKind == JsonValueKind.True;
            var underline = Field(style, "underline").ValueKind == JsonValueKind.True;
            if (bold) writer.Add("<strong>");
            if (italic) writer.Add("<em>");
            if (strike) writer.Add("<s>");
            if (underline) writer.Add("<u>");
            RenderWords(String(Required(word, "words")), writer);
            if (underline) writer.Add("</u>");
            if (strike) writer.Add("</s>");
            if (italic) writer.Add("</em>");
            if (bold) writer.Add("</strong>");
            return;
        }
        var rich = Field(node, "rich");
        if (rich.ValueKind == JsonValueKind.Object)
        {
            var text = Field(rich, "text");
            var richUrl = Field(rich, "jump_url");
            if (text.ValueKind == JsonValueKind.String && richUrl.ValueKind == JsonValueKind.String)
            {
                var url = HttpUrl(richUrl.GetString()!);
                writer.Add("<a href=\"" + WebUtility.HtmlEncode(url) + "\">");
                RenderWords(text.GetString()!, writer);
                writer.Add("</a>");
                return;
            }
        }
        var link = Field(node, "link");
        var showText = Field(link, "show_text");
        if (showText.ValueKind == JsonValueKind.String)
        {
            // Numeric node_type=3 includes mentions with biz_id, not necessarily a web URL.
            // Preserve the visible mention without inventing a link or executing an app scheme.
            RenderWords(showText.GetString()!, writer);
            return;
        }
        throw Fail(ArticleCompatibilityFailure.UnsupportedNode);
    }

    private static void RenderOpusPictures(JsonElement pictures, BodyWriter writer, string articleId,
        string path, Action<ArticleParseNotice>? notice)
    {
        var index = 0;
        foreach (var picture in pictures.EnumerateArray())
        {
            var itemPath = path + "[" + (index++).ToString(CultureInfo.InvariantCulture) + "]";
            RecoverContent(picture, writer, articleId, itemPath, "picture", true, notice,
                () => RenderPicture(picture, writer, meaningful: true));
        }
    }

    private static void RenderOpusList(JsonElement list, BodyWriter writer, string articleId,
        string path, Action<ArticleParseNotice>? notice)
    {
        var ordered = Field(list, "style").ValueKind == JsonValueKind.Number && Integer(Field(list, "style")) == 2;
        writer.Add(ordered ? "<ol>" : "<ul>");
        var index = 0;
        foreach (var item in Array(Required(list, "items")).EnumerateArray())
        {
            writer.Add("<li>");
            var itemPath = path + ".items[" + (index++).ToString(CultureInfo.InvariantCulture) + "]";
            RecoverContent(item, writer, articleId, itemPath, "list-item", false, notice,
                () => RenderOpusNodes(Array(Required(item, "nodes")), writer, articleId, itemPath + ".nodes", notice));
            writer.Add("</li>");
        }
        writer.Add(ordered ? "</ol>" : "</ul>");
    }

    private static void RenderOpusCard(JsonElement card, BodyWriter writer)
    {
        foreach (var property in Object(card).EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            var title = Field(property.Value, "title");
            if (title.ValueKind != JsonValueKind.String) title = Field(property.Value, "text");
            if (title.ValueKind != JsonValueKind.String) continue;
            var url = Field(property.Value, "jump_url");
            writer.Add("<p>");
            if (url.ValueKind == JsonValueKind.String)
                writer.Add("<a href=\"" + WebUtility.HtmlEncode(HttpUrl(url.GetString()!)) + "\">");
            RenderWords(title.GetString()!, writer);
            if (url.ValueKind == JsonValueKind.String) writer.Add("</a>");
            writer.Add("</p>");
            return;
        }
        throw Fail(ArticleCompatibilityFailure.UnsupportedParagraph);
    }

    private static void RecoverContent(JsonElement value, BodyWriter writer, string articleId, string path,
        string type, bool block, Action<ArticleParseNotice>? notice, Action render)
    {
        writer.Visit();
        var mark = writer.Mark();
        try { render(); }
        catch (ArticleCompatibilityException ex) when (ex.Failure != ArticleCompatibilityFailure.LimitExceeded)
        {
            // Roll back partial tags before degrading this unit, keeping adjacent content intact.
            writer.Restore(mark);
            notice?.Invoke(new(articleId, path, type, ex.Failure));
            var text = new StringBuilder();
            ExtractPlainText(value, text, writer);
            if (block) writer.Add("<p>");
            if (string.IsNullOrWhiteSpace(text.ToString())) writer.Add("[不支持的内容]");
            else RenderWords(text.ToString(), writer);
            if (block) writer.Add("</p>");
            writer.Meaningful = true; // A visible unsupported-content placeholder is not an empty body.
        }
    }

    private static void ExtractPlainText(JsonElement value, StringBuilder output, BodyWriter writer)
    {
        writer.Visit();
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray()) ExtractPlainText(item, output, writer);
        }
        else if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in value.EnumerateObject())
            {
                // Only semantic text fields: never expose IDs, tracking URLs, raw HTML or scripts.
                if (property.Name is "words" or "text" or "show_text" or "title" or "name" or "desc" or "content" or "latex" or "label" or "question")
                {
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        var text = property.Value.GetString()!;
                        if (string.IsNullOrWhiteSpace(text)) continue;
                        if (output.Length != 0) Append(output, "\n");
                        Append(output, text);
                    }
                    else ExtractPlainText(property.Value, output, writer);
                }
                else if (property.Name is "word" or "rich" or "nodes" or "paragraphs" or "items" or "vote" or "options"
                    or "formula" or "code" or "user" or "emoji" or "link" or "link_card" or "card" or "opus" or "archive" or "video"
                    or "option" or "blockquote" or "list" or "heading")
                    ExtractPlainText(property.Value, output, writer);
            }
        }
    }

    private static string TypeLabel(JsonElement type)
    {
        if (type.ValueKind == JsonValueKind.Number && type.TryGetInt64(out var number)) return number.ToString(CultureInfo.InvariantCulture);
        if (type.ValueKind == JsonValueKind.String && type.GetString() is { Length: > 0 and <= 80 } text
            && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) return text;
        return type.ValueKind.ToString();
    }
}
