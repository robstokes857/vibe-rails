using System.Text.RegularExpressions;

namespace Tests.Services.VCA;

/// <summary>
/// Strips ANSI escape sequences so tests can assert on the visible text of styled
/// console output (gradients insert a color code before every character, breaking
/// naive substring assertions).
/// </summary>
internal static partial class AnsiText
{
    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex EscapeSequences();

    public static string Strip(string text) => EscapeSequences().Replace(text, "");
}
