using System.Text;
using System.Text.RegularExpressions;

namespace Tests.Services;

/// <summary>
/// Reads generated PowerShell back as text a test can search: each
/// <c>ShellArgSanitizer.DecodedPowerShellLiteral</c> expression becomes the single-quoted value it
/// decodes to. Text without one (POSIX output) comes back unchanged.
/// </summary>
internal static partial class PowerShellLiteralText
{
    [GeneratedRegex(@"\(\[System\.Text\.Encoding\]::UTF8\.GetString\(\[System\.Convert\]::FromBase64String\('([A-Za-z0-9+/=]*)'\)\)\)")]
    private static partial Regex DecodedLiteral();

    public static string Decode(string text) => DecodedLiteral().Replace(
        text, match => "'" + Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value)) + "'");
}
