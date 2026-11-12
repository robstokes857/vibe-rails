using System.Globalization;
using System.Text;

namespace VibeRails.Utils;

/// <summary>
/// Request-line text for the request log. That log line is written before authentication, so the
/// text belongs to whoever can reach the port. The path arrives percent-decoded: a <c>%0A</c> in it
/// would otherwise start a second, forged record in <c>vb-*.log</c>, which the Logs panel and any
/// agent asked to read the logs would take for a real one. Controls and Unicode display controls
/// are made visible and inert, and each part is bounded. VIBE-310.
/// </summary>
public static class RequestLogText
{
    public const int MaxLength = 512;

    public static string Path(string? value) => Bound(Escape(value ?? string.Empty));

    public static string Query(string? value) => Bound(Escape(value ?? string.Empty));

    private static string Escape(string value)
    {
        // Explicit CR/LF replacement also makes the log-forging boundary visible to CodeQL.
        var line = value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
        StringBuilder? safe = null;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            var category = char.GetUnicodeCategory(character);
            var unsafeCharacter = char.IsControl(character) || category is UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;
            if (!unsafeCharacter)
            {
                safe?.Append(character);
                continue;
            }
            safe ??= new StringBuilder(line, 0, index, line.Length + 16);
            safe.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
        }
        return safe?.ToString() ?? line;
    }

    private static string Bound(string value) =>
        value.Length <= MaxLength
            ? value
            : string.Concat(value.AsSpan(0, MaxLength), "…(+", (value.Length - MaxLength).ToString(CultureInfo.InvariantCulture), ")");
}
