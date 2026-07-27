using System.Globalization;
using System.Text;

namespace VibeRails.Services.Terminal;

internal static class TerminalControlProtocol
{
    public const int MaxMessageBytes = 256 * 1024;
    public const int MaxControlReasonLength = 120;
    public const int MaxCommandNameLength = 64;
    public const int MaxCommandPayloadLength = 8 * 1024;
    public const string CommandPrefix = "__cmd__:";
    // Legacy frame kept for older remote/relay clients that haven't picked up
    // the structured __cmd__:replay form yet. TODO: remove once the marketplace
    // VS Code extension and any deployed relays are confirmed past 1.7.x.
    public const string ReplayCommand = "__replay__";
    public const string ReplayCommandName = "replay";
    public const string ReplayCommandFrame = CommandPrefix + ReplayCommandName;
    public const string BrowserDisconnectedCommand = "__browser_disconnected__";
    public const string DisconnectBrowserCommand = "__disconnect_browser__";
    public const string ResizePrefix = "__resize__:";

    // PIN challenge protocol — sent as plain text (not __cmd__:) to keep it simple
    // and match the remote app's expected message format.
    public const string Locked = "__LOCKED__";
    public const string Unlocked = "__UNLOCKED__";
    public const string PinResponse = "__PIN__:";

    public static string BuildDisconnectBrowserCommand(string reason)
    {
        var safeReason = SanitizeReason(reason, "Session taken over by local viewer");
        return $"{DisconnectBrowserCommand}:{safeReason}";
    }

    // Every reserved frame the viewer protocol defines. A message starting with any
    // of these is protocol traffic, never keystrokes — see IsReservedControlFrame.
    private static readonly string[] s_reservedPrefixes =
    [
        ResizePrefix,
        CommandPrefix,
        ReplayCommand,
        BrowserDisconnectedCommand,
        DisconnectBrowserCommand,
        PinResponse,
        Locked,
        Unlocked
    ];

    /// <summary>
    /// True when the message starts with any reserved control prefix. Such a frame
    /// must NEVER be routed to the PTY, even when its payload fails to validate —
    /// writing it types the literal text into whatever TUI is running. Session
    /// 71dee36a: a 4-row fit produced <c>__resize__:171,4</c>, which fails the
    /// rows &gt;= 5 bound in <see cref="TryParseResizeCommand"/> and was typed into
    /// OpenCode's composer.
    /// <para>
    /// Callers must check this AFTER attempting to parse, and drop rather than route,
    /// so well-formed frames still reach their handlers. Covers the exact-match frames
    /// too (<c>__replay__</c>, <c>__PIN__:</c>, …) because a malformed variant of one —
    /// <c>__replay__x</c>, or a <c>__PIN__:</c> arriving after the handshake — would
    /// otherwise fall through and be typed into the TUI. No current client emits those,
    /// but a stray PIN reaching a terminal is not a failure mode worth leaving open.
    /// </para>
    /// <para>
    /// EXCEPTION — the remote receive loop must forward <c>__PIN__:</c> frames to
    /// <c>OnInputReceived</c> BEFORE consulting this method: on that path the PIN
    /// handshake rides the input channel and is consumed by TerminalRunner's verifier
    /// downstream (which drops strays after authorization). Treating it as reserved
    /// there makes PIN-protected remote sessions impossible to unlock.
    /// </para>
    /// </summary>
    public static bool IsReservedControlFrame(string input)
    {
        foreach (var prefix in s_reservedPrefixes)
        {
            if (input.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when the raw frame bytes start with the <c>__PIN__:</c> prefix. Byte-level so
    /// callers on the input hot path can check without decoding every keystroke; the
    /// prefix is pure ASCII, so a byte compare is exact.
    /// </summary>
    public static bool IsPinResponseFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < PinResponse.Length) return false;
        for (var i = 0; i < PinResponse.Length; i++)
        {
            if (frame[i] != (byte)PinResponse[i]) return false;
        }

        return true;
    }

    /// <summary>
    /// Renders a rejected frame safe to log: redacts PIN payloads (the digits are a
    /// secret and must never be persisted), strips control characters (a malformed
    /// frame can carry ANSI escapes, which must not land unescaped in the log) and
    /// truncates. Same concern and filter as <see cref="SanitizeReason"/>.
    /// </summary>
    public static string SanitizeFrameForLog(string frame, int maxLength = 64)
    {
        if (frame.StartsWith(PinResponse, StringComparison.Ordinal))
            return PinResponse + "<redacted>";

        var sb = new StringBuilder(Math.Min(frame.Length, maxLength));
        foreach (var ch in frame)
        {
            if (sb.Length >= maxLength) break;
            if (!char.IsControl(ch)) sb.Append(ch);
        }

        return sb.ToString();
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
            command = body;
        }
        else
        {
            command = body[..splitIndex];
            payload = body[(splitIndex + 1)..];
        }

        if (!IsValidCommandName(command))
            return false;

        if (payload is { Length: > MaxCommandPayloadLength })
            return false;

        return true;
    }

    public static bool IsReplayCommand(string input)
    {
        if (string.Equals(input, ReplayCommand, StringComparison.Ordinal))
            return true;

        return TryParseCommand(input, out var command, out var payload)
            && payload is null
            && string.Equals(command, ReplayCommandName, StringComparison.Ordinal);
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
