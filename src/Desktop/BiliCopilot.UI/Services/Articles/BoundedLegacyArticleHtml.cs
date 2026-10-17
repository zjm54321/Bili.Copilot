// Copyright (c) Bili Copilot. All rights reserved.

using System.Net;

namespace BiliCopilot.UI.Services.Articles;

/// <summary>
/// A deliberately small, balanced HTML-subset reader, NOT a general HTML sanitizer.
/// No original tag or attribute is emitted. Unsupported syntax/content fails closed.
/// </summary>
internal sealed class BoundedLegacyArticleHtml
{
    private const int MaximumDepth = 32;
    private const int MaximumOperations = 16000;
    private readonly string _input;
    private readonly CancellationToken _cancellationToken;
    private readonly StringBuilder _output = new();
    private readonly Stack<Tag> _stack = new();
    private int _position;
    private int _operations;
    private bool _meaningful;

    private BoundedLegacyArticleHtml(string input, CancellationToken cancellationToken)
    {
        _input = input;
        _cancellationToken = cancellationToken;
    }

    internal static string Render(string input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (input.Length > ArticleContentParser.MaximumJsonCharacters)
            throw Fail(ArticleCompatibilityFailure.LimitExceeded);
        return new BoundedLegacyArticleHtml(input, cancellationToken).Read();
    }

    private string Read()
    {
        while (_position < _input.Length)
        {
            Visit();
            if (_input[_position] != '<') ReadText();
            else ReadTag();
        }
        if (_stack.Count != 0) throw Fail(ArticleCompatibilityFailure.MalformedData);
        if (!_meaningful) throw Fail(ArticleCompatibilityFailure.MissingContent);
        return _output.ToString();
    }

    private void ReadText()
    {
        var start = _position;
        while (_position < _input.Length && _input[_position] != '<') Advance();
        var text = Decode(_input[start.._position]);
        if (_stack.TryPeek(out var parent) && parent.Name is "ul" or "ol" && !string.IsNullOrWhiteSpace(text))
            throw Fail(ArticleCompatibilityFailure.MalformedData);
        Add(WebUtility.HtmlEncode(text));
        _meaningful |= !string.IsNullOrWhiteSpace(text);
    }

    private void ReadTag()
    {
        Require('<');
        var closing = Take('/');
        // '<!', '<?', comments, raw-text elements, foreign content and unknown tags
        // have no production in this grammar. A literal '<' must be entity-encoded.
        var tag = Grammar(Name());
        if (closing)
        {
            WhiteSpace();
            Require('>');
            if (tag.Void || !_stack.TryPop(out var opened) || opened.Name != tag.Name)
                throw Fail(ArticleCompatibilityFailure.MalformedData);
            Add(opened.Close);
            return;
        }

        string? url = null;
        string? alt = null;
        string? title = null;
        var attributes = new HashSet<string>(StringComparer.Ordinal);
        var selfClosing = false;
        while (true)
        {
            var separated = WhiteSpace();
            if (Take('>')) break;
            if (Take('/'))
            {
                Require('>');
                selfClosing = true;
                break;
            }
            if (!separated) throw Fail(ArticleCompatibilityFailure.MalformedData);
            Visit();
            var name = Name();
            if (!attributes.Add(name)) throw Fail(ArticleCompatibilityFailure.MalformedData);
            if (attributes.Count > 32) throw Fail(ArticleCompatibilityFailure.LimitExceeded);
            WhiteSpace();
            Require('=');
            WhiteSpace();
            var value = AttributeValue();
            switch (name)
            {
                case "href" when tag.Name == "a":
                    url = ArticleContentParser.HttpUrl(value);
                    break;
                case "src" or "data-src" when tag.Name == "img":
                    // The pinned SDK maps data-src to src. Accept either populated slot,
                    // but never silently replace a different/unsafe destination in the other.
                    if (string.IsNullOrWhiteSpace(value)) break;
                    var imageUrl = ArticleContentParser.HttpUrl(value);
                    if (url is not null && !string.Equals(url, imageUrl, StringComparison.Ordinal))
                        throw Fail(ArticleCompatibilityFailure.UnsafeUrl);
                    url = imageUrl;
                    break;
                case "alt" when tag.Name == "img": alt = value; break;
                case "title": title = value; break;
                case "class": break; // Recognized presentation-only metadata; never forwarded.
                case "style": break; // Parsed as quoted text only; no CSS is interpreted or emitted.
                case "contenteditable" when string.Equals(value, "false", StringComparison.OrdinalIgnoreCase):
                case "data-size" when tag.Name == "img":
                    break; // Known editor/image metadata from the pinned SDK, never forwarded.
                case "width" or "height" when tag.Name == "img":
                    if (value.Length is 0 or > 5 || !value.All(char.IsAsciiDigit))
                        throw Fail(ArticleCompatibilityFailure.UnsupportedHtml);
                    break; // Dimensions do not grant caller-controlled CSS/attributes.
                default: throw Fail(ArticleCompatibilityFailure.UnsupportedHtml);
            }
        }
        if (selfClosing && !tag.Void) throw Fail(ArticleCompatibilityFailure.MalformedData);
        if (tag.Name is "a" or "img" && url is null) throw Fail(ArticleCompatibilityFailure.UnsafeUrl);
        CheckNesting(tag);
        if (!tag.Void)
        {
            if (_stack.Count >= MaximumDepth) throw Fail(ArticleCompatibilityFailure.LimitExceeded);
            _stack.Push(tag);
        }
        // Open/Close come ONLY from the literal table below; all variable slots are encoded.
        Add(tag.Open);
        if (tag.Name == "a") Add(" href=\"" + WebUtility.HtmlEncode(url) + "\"");
        if (tag.Name == "img")
        {
            Add(" src=\"" + WebUtility.HtmlEncode(url) + "\" alt=\"" + WebUtility.HtmlEncode(alt ?? string.Empty) + "\"");
            _meaningful = true;
        }
        if (title is not null) Add(" title=\"" + WebUtility.HtmlEncode(title) + "\"");
        Add(tag.Void ? " />" : ">");
    }

    private void CheckNesting(Tag tag)
    {
        _stack.TryPeek(out var parent);
        if ((parent?.Name is "ul" or "ol" && tag.Name != "li")
            || (tag.Name == "li" && parent?.Name is not ("ul" or "ol"))
            || (tag.Name == "figcaption" && parent?.Name != "figure"))
            throw Fail(ArticleCompatibilityFailure.MalformedData);
        foreach (var ancestor in _stack)
        {
            if ((tag.Name == "a" && ancestor.Name == "a")
                || (!tag.Inline && (ancestor.Inline || ancestor.Name is "p" or "pre" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6")))
                throw Fail(ArticleCompatibilityFailure.MalformedData);
        }
    }

    private string AttributeValue()
    {
        if (_position >= _input.Length || _input[_position] is not ('\'' or '"'))
            throw Fail(ArticleCompatibilityFailure.MalformedData);
        var quote = _input[_position++];
        var start = _position;
        while (_position < _input.Length && _input[_position] != quote)
        {
            // No browser-style error recovery or quote/backslash interpretation.
            if (_input[_position] == '<') throw Fail(ArticleCompatibilityFailure.MalformedData);
            Advance();
        }
        var value = Decode(_input[start.._position]);
        Require(quote);
        return value;
    }

    private string Name()
    {
        var start = _position;
        if (_position >= _input.Length || !char.IsAsciiLetter(_input[_position]))
            throw Fail(ArticleCompatibilityFailure.MalformedData);
        while (_position < _input.Length && (char.IsAsciiLetterOrDigit(_input[_position]) || _input[_position] == '-'))
        {
            if (_position - start >= 64) throw Fail(ArticleCompatibilityFailure.LimitExceeded);
            Advance();
        }
        return _input[start.._position].ToLowerInvariant();
    }

    private bool WhiteSpace()
    {
        var start = _position;
        while (_position < _input.Length && _input[_position] is ' ' or '\t' or '\r' or '\n') Advance();
        return start != _position;
    }

    private bool Take(char value)
    {
        if (_position >= _input.Length || _input[_position] != value) return false;
        Advance();
        return true;
    }

    private void Require(char value)
    {
        if (!Take(value)) throw Fail(ArticleCompatibilityFailure.MalformedData);
    }

    private void Advance()
    {
        if ((_position & 1023) == 0) _cancellationToken.ThrowIfCancellationRequested();
        var character = _input[_position++];
        if (char.IsControl(character) && character is not ('\t' or '\r' or '\n'))
            throw Fail(ArticleCompatibilityFailure.MalformedData);
    }

    private string Decode(string value)
    {
        var text = WebUtility.HtmlDecode(value);
        for (var i = 0; i < text.Length; i++)
        {
            if ((i & 1023) == 0) _cancellationToken.ThrowIfCancellationRequested();
            if (char.IsControl(text[i]) && text[i] is not ('\t' or '\r' or '\n'))
                throw Fail(ArticleCompatibilityFailure.MalformedData);
        }
        return text;
    }

    private void Visit()
    {
        _cancellationToken.ThrowIfCancellationRequested();
        if (++_operations > MaximumOperations) throw Fail(ArticleCompatibilityFailure.LimitExceeded);
    }

    private void Add(string text)
    {
        if (text.Length > ArticleContentParser.MaximumOutputCharacters - _output.Length)
            throw Fail(ArticleCompatibilityFailure.LimitExceeded);
        _output.Append(text);
    }

    private sealed record Tag(string Name, string Open, string Close, bool Inline, bool Void = false);

    private static Tag Grammar(string name) => name switch
    {
        "p" => new("p", "<p", "</p>", false),
        "div" => new("div", "<div", "</div>", false),
        "span" => new("span", "<span", "</span>", true),
        "h1" => new("h1", "<h1", "</h1>", false),
        "h2" => new("h2", "<h2", "</h2>", false),
        "h3" => new("h3", "<h3", "</h3>", false),
        "h4" => new("h4", "<h4", "</h4>", false),
        "h5" => new("h5", "<h5", "</h5>", false),
        "h6" => new("h6", "<h6", "</h6>", false),
        "b" => new("b", "<strong", "</strong>", true),
        "strong" => new("strong", "<strong", "</strong>", true),
        "i" => new("i", "<em", "</em>", true),
        "em" => new("em", "<em", "</em>", true),
        "u" => new("u", "<u", "</u>", true),
        "s" => new("s", "<s", "</s>", true),
        "blockquote" => new("blockquote", "<blockquote", "</blockquote>", false),
        "pre" => new("pre", "<pre", "</pre>", false),
        "code" => new("code", "<code", "</code>", true),
        "ul" => new("ul", "<ul", "</ul>", false),
        "ol" => new("ol", "<ol", "</ol>", false),
        "li" => new("li", "<li", "</li>", false),
        "figure" => new("figure", "<figure", "</figure>", false),
        "figcaption" => new("figcaption", "<figcaption", "</figcaption>", false),
        "a" => new("a", "<a", "</a>", true),
        "img" => new("img", "<img", string.Empty, true, true),
        "br" => new("br", "<br", string.Empty, true, true),
        "hr" => new("hr", "<hr", string.Empty, false, true),
        _ => throw Fail(ArticleCompatibilityFailure.UnsupportedHtml),
    };

    private static ArticleCompatibilityException Fail(ArticleCompatibilityFailure failure) => new(failure);
}
