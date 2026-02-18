using System.Globalization;
using System.Text;

namespace VibeRails.Services.Terminal;

internal static class TerminalControlProtocol
{
    public const int MaxMessageBytes = 256 * 1024;
    public const int MaxControlReasonLength = 120;
    public const int MaxCommandNameLength = 64;
    public const int MaxCommandPayloadLength = 8 * 1024;
    public const string ReplayCommand = "__replay__";
    public const string BrowserDisconnectedCommand = "__browser_disconnected__";
    public const string DisconnectBrowserCommand = "__disconnect_browser__";
    public const string ResizePrefix = "__resize__:";
    public const string CommandPrefix = "__cmd__:";

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

    public static string BuildCommand(string command, string? payload = null)
    {
        if (!IsValidCommandName(command))
            throw new ArgumentException($"Invalid command name: {command}", nameof(command));

        if (payload is { Length: > MaxCommandPayloadLength })
            throw new ArgumentException("Command payload is too large", nameof(payload));

        return payload is null
            ? $"{CommandPrefix}{command}"
            : $"{CommandPrefix}{command}:{payload}";
    }

    public static bool TryParseCommand(string input, out string command, out string? payload)
    {
        command = string.Empty;
        payload = null;

        if (!input.StartsWith(CommandPrefix, StringComparison.Ordinal))
            return false;

        var body = input[CommandPrefix.Length..];
        if (string.IsNullOrWhiteSpace(body))
            return false;

        var splitIndex = body.IndexOf(':');
        if (splitIndex < 0)
        {
            command = body.Trim();
        }
        else
        {
            command = body[..splitIndex].Trim();
            payload = body[(splitIndex + 1)..];
        }

        if (!IsValidCommandName(command))
            return false;

        if (payload is { Length: > MaxCommandPayloadLength })
            return false;

        return true;
    }

    private static bool IsValidCommandName(string command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.Length > MaxCommandNameLength)
            return false;

        foreach (var ch in command)
        {
            var ok = char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.';
            if (!ok)
                return false;
        }

        return true;
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
