// Copyright (c) Bili Copilot. All rights reserved.

using Microsoft.UI.Xaml.Documents;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BiliCopilot.UI.Controls.Message;

/// <summary>Renders system-message web text without interpreting HTML or executable URI schemes.</summary>
public sealed partial class SystemMessageText : DependencyObject
{
    /// <summary>The source text; a property callback also handles recycled message templates.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(SystemMessageText), new PropertyMetadata(null, OnTextChanged));

    /// <summary>Gets the source text.</summary>
    public static string? GetText(DependencyObject element) => (string?)element.GetValue(TextProperty);

    /// <summary>Sets the source text.</summary>
    public static void SetText(DependencyObject element, string? value) => element.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is not RichTextBlock block) return;
        block.Blocks.Clear();
        var paragraph = new Paragraph();
        var text = args.NewValue as string ?? string.Empty;
        var position = 0;
        foreach (Match match in LinkPattern().Matches(text))
        {
            AddText(paragraph, text[position..match.Index]);
            if (match.Groups["label"].Success)
            {
                var target = DecodeTarget(match.Groups["target"].Value);
                AddLinkOrText(paragraph, match.Groups["label"].Value, target, match.Value);
            }
            else
            {
                var url = TrimUrlEnd(match.Value);
                AddLinkOrText(paragraph, url, url, url);
                AddText(paragraph, match.Value[url.Length..]);
            }

            position = match.Index + match.Length;
        }

        AddText(paragraph, text[position..]);
        block.Blocks.Add(paragraph);
    }

    private static void AddLinkOrText(Paragraph paragraph, string label, string? target, string fallback)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host))
        {
            // WinUI launches NavigateUri using the system's browser, not an in-app WebView.
            var link = new Hyperlink { NavigateUri = uri };
            link.Inlines.Add(new Run { Text = label });
            paragraph.Inlines.Add(link);
        }
        else
        {
            AddText(paragraph, fallback);
        }
    }

    private static void AddText(Paragraph paragraph, string text)
    {
        if (text.Length > 0) paragraph.Inlines.Add(new Run { Text = text });
    }

    private static string? DecodeTarget(string target)
    {
        target = target.Trim();
        if (!target.StartsWith('"')) return target;
        try
        {
            using var json = JsonDocument.Parse(target);
            return json.RootElement.ValueKind == JsonValueKind.String ? json.RootElement.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string TrimUrlEnd(string text)
    {
        var end = text.Length;
        while (end > 0)
        {
            var last = text[end - 1];
            if (".,;:!?".Contains(last)
                || (last == ')' && text[..end].Count(c => c == ')') > text[..end].Count(c => c == '('))
                || (last == ']' && text[..end].Count(c => c == ']') > text[..end].Count(c => c == '[')))
            {
                end--;
            }
            else
            {
                break;
            }
        }

        return text[..end];
    }

    [GeneratedRegex("""#\{(?<label>[^{}]*)\}\{(?<target>[^{}]*)\}|https?://[^\s<>"'{}，。！？；、（）【】“”‘’]+""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LinkPattern();
}
