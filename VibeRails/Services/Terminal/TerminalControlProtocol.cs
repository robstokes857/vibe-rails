using System.Globalization;
using System.Text;

namespace VibeRails.Services.Terminal;

internal static class TerminalControlProtocol
{
    public const int MaxMessageBytes = 256 * 1024;
    public const int MaxControlReasonLength = 120;
    public const string ReplayCommand = "__replay__";
    public const string BrowserDisconnectedCommand = "__browser_disconnected__";
    public const string DisconnectBrowserCommand = "__disconnect_browser__";
    public const string ResizePrefix = "__resize__:";

    public static string BuildDisconnectBrowserCommand(string reason)
    {
        var safeReason = SanitizeReason(reason, "Session taken over by local viewer");
        return $"{DisconnectBrowserCommand}:{safeReason}";
    }

    public static bool TryParseResizeCommand(string input, out int cols, out int rows)
    {
        cols = 0;
        rows = 0;

        if (!input.StartsWith(ResizePrefix, StringComparison.Ordinal))
            return false;

        var payload = input[ResizePrefix.Length..];
        var parts = payload.Split(',', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2)
            return false;

        if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out cols))
            return false;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out rows))
            return false;

        return cols is >= 10 and <= 1000 && rows is >= 5 and <= 500;
    }

    private static string SanitizeReason(string reason, string fallback)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return fallback;

        var trimmed = reason.Trim();
        var sb = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (!char.IsControl(ch))
                sb.Append(ch);
        }

        var sanitized = sb.ToString();
        if (string.IsNullOrWhiteSpace(sanitized))
            return fallback;

        if (sanitized.Length > MaxControlReasonLength)
            sanitized = sanitized[..MaxControlReasonLength];

        return sanitized;
    }
}
