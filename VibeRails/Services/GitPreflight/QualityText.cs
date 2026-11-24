using System.Globalization;
using System.Text;

namespace VibeRails.Services.GitPreflight;

/// <summary>How Code quality numbers and names from analysed code are written in reports.</summary>
internal static class QualityText
{
    /// <summary>The Quality page's <c>formatNumber</c>: en-US grouping, at most <paramref name="digits"/> decimals.</summary>
    public static string Format(double value, int digits) =>
        double.IsFinite(value) ? value.ToString(digits == 0 ? "#,0" : "#,0." + new string('#', digits), CultureInfo.InvariantCulture) : "—";

    /// <summary>Text from analysed code, made safe to print on one line: no control or bidi characters, bounded.</summary>
    public static string OneLine(string? value, int maxChars)
    {
        var builder = new StringBuilder();
        foreach (var c in value ?? "")
        {
            if (c is '\n' or '\r' or '\t') builder.Append(' ');
            else if (!char.IsControl(c) && !IsBidiControl(c)) builder.Append(c);
        }
        var text = builder.ToString().Trim();
        while (text.Contains("  ", StringComparison.Ordinal)) text = text.Replace("  ", " ");
        return text.Length <= maxChars ? text : string.Concat(text.AsSpan(0, Math.Max(0, maxChars - 1)), "…");
    }

    // Marks (U+200E, U+200F), embeddings and overrides (U+202A to U+202E) and isolates (U+2066 to U+2069).
    private static bool IsBidiControl(char c) => (int)c is 0x200E or 0x200F or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069);
}
