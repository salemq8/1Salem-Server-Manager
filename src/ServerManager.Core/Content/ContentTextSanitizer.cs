using System.Text;
using System.Text.RegularExpressions;

namespace ServerManager.Core.Content;

/// <summary>
/// Turns a provider's project description into plain text. Descriptions are Markdown or HTML
/// written by other people, so nothing from them is ever rendered as markup: tags, scripts,
/// images and links are reduced to their readable text and nothing else.
/// </summary>
public static partial class ContentTextSanitizer
{
    private const int MaximumLength = 4000;

    public static string ToPlainText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value;

        // Script and style blocks go entirely, contents included.
        text = ScriptOrStyle().Replace(text, " ");

        // Markdown images and links keep their caption, never the target.
        text = MarkdownImage().Replace(text, " ");
        text = MarkdownLink().Replace(text, "$1");

        // Fenced code, inline code markers and heading or emphasis punctuation.
        text = FencedCode().Replace(text, " ");
        text = HtmlTag().Replace(text, " ");
        text = MarkdownDecoration().Replace(text, string.Empty);

        text = System.Net.WebUtility.HtmlDecode(text);

        // A bare URL left in the prose is not clickable anywhere, but it is noise.
        text = BareUrl().Replace(text, string.Empty);

        var builder = new StringBuilder(text.Length);
        var lastWasSpace = false;
        foreach (var character in text)
        {
            if (char.IsControl(character) && character is not '\n')
            {
                continue;
            }

            var isSpace = char.IsWhiteSpace(character);
            if (isSpace)
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                }
            }
            else
            {
                builder.Append(character);
            }

            lastWasSpace = isSpace;
        }

        var result = builder.ToString().Trim();
        return result.Length > MaximumLength ? result[..MaximumLength].TrimEnd() + "…" : result;
    }

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptOrStyle();

    [GeneratedRegex(@"!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"```.*?```", RegexOptions.Singleline)]
    private static partial Regex FencedCode();

    [GeneratedRegex(@"<[^>]{1,2000}>", RegexOptions.Singleline)]
    private static partial Regex HtmlTag();

    [GeneratedRegex(@"[*_`>#|]+")]
    private static partial Regex MarkdownDecoration();

    [GeneratedRegex(@"\b(https?|javascript|data)\s*:\s*\S+", RegexOptions.IgnoreCase)]
    private static partial Regex BareUrl();
}
