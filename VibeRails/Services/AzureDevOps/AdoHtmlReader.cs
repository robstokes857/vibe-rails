using System.Net;
using System.Text;

namespace VibeRails.Services.AzureDevOps;

/// <summary>
/// Azure DevOps rich text (description, repro steps, acceptance criteria) as plain card text. A
/// single linear scan, with no regex and no reflection, so it is AOT-safe and cheap on the large
/// bodies pasted images produce. Block tags become line breaks, list items become <c>- </c> lines,
/// script, style and any other tag are dropped, and entities are decoded last, so an encoded
/// <c>&amp;lt;tag&amp;gt;</c> stays as text.
/// </summary>
public static class AdoHtmlReader
{
    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "ul", "ol", "li", "h1", "h2", "h3", "h4", "h5", "h6", "tr", "table", "thead", "tbody",
        "blockquote", "pre", "hr", "section", "article", "header", "footer", "dl", "dt", "dd"
    };

    public static string ToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;
        var builder = new StringBuilder(html.Length);
        var preformatted = 0;
        // The first '>' at or after the scan position, or -1 for none. Reused until the scan
        // passes it, so a run of '<' with no '>' after them is not a search per '<'.
        var nextClose = 0;
        var i = 0;
        while (i < html.Length)
        {
            var c = html[i];
            if (c != '<')
            {
                if (preformatted > 0 || !char.IsWhiteSpace(c))
                    builder.Append(c);
                else if (builder.Length > 0 && builder[^1] is not (' ' or '\n'))
                    builder.Append(' ');
                i++;
                continue;
            }

            if (nextClose >= 0 && nextClose <= i)
                nextClose = html.IndexOf('>', i + 1);
            var close = nextClose;
            if (close < 0 || i + 1 >= html.Length || !(char.IsAsciiLetter(html[i + 1]) || html[i + 1] is '/' or '!' or '?'))
            {
                // A stray '<' that starts no tag is text.
                builder.Append(c);
                i++;
                continue;
            }
            var (name, closing) = TagName(html, i + 1, close);
            i = close + 1;
            if (name.Length == 0)
                continue;
            if (!closing && (name.Equals("script", StringComparison.OrdinalIgnoreCase) || name.Equals("style", StringComparison.OrdinalIgnoreCase)))
            {
                var end = html.IndexOf("</" + name, i, StringComparison.OrdinalIgnoreCase);
                var endClose = end < 0 ? -1 : html.IndexOf('>', end);
                i = endClose < 0 ? html.Length : endClose + 1;
                continue;
            }
            if (name.Equals("br", StringComparison.OrdinalIgnoreCase))
            {
                builder.Append('\n');
                continue;
            }
            if (name.Equals("pre", StringComparison.OrdinalIgnoreCase))
                preformatted = Math.Max(0, preformatted + (closing ? -1 : 1));
            if (BlockTags.Contains(name))
            {
                NewLine(builder);
                if (!closing && name.Equals("li", StringComparison.OrdinalIgnoreCase))
                    builder.Append("- ");
            }
        }
        return Tidy(WebUtility.HtmlDecode(builder.ToString()));
    }

    private static (string Name, bool Closing) TagName(string html, int start, int end)
    {
        var at = start;
        var closing = at < end && html[at] == '/';
        if (closing) at++;
        var from = at;
        while (at < end && (char.IsAsciiLetterOrDigit(html[at]) || html[at] == '-'))
            at++;
        // Comments and declarations (<!-- -->, <!DOCTYPE>) have no name and are dropped.
        return (html[from..at], closing);
    }

    private static void NewLine(StringBuilder builder)
    {
        while (builder.Length > 0 && builder[^1] == ' ')
            builder.Length--;
        if (builder.Length > 0 && builder[^1] != '\n')
            builder.Append('\n');
    }

    // Non-breaking spaces read as spaces, each line loses trailing spaces, and runs of blank lines
    // shrink to one.
    private static string Tidy(string text)
    {
        var lines = text.Replace(' ', ' ').Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var builder = new StringBuilder(text.Length);
        var blank = 0;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Trim().Length == 0)
            {
                blank++;
                continue;
            }
            if (builder.Length > 0)
                builder.Append(blank > 0 ? "\n\n" : "\n");
            blank = 0;
            builder.Append(line);
        }
        return builder.ToString().Trim();
    }
}
