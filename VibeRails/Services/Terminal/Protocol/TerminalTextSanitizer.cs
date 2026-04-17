using System.Globalization;
using System.Text;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Converts raw terminal text to plain text by stripping ANSI escape/control
/// sequences and non-printable control characters.
/// </summary>
public static class TerminalTextSanitizer
{
    public static string ToPlainText(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        var parts = ToTextWithControl(input);
        if (parts.Count == 0)
            return string.Empty;

        var sb = new StringBuilder(input.Length);
        foreach (var part in parts)
        {
            if (!string.IsNullOrEmpty(part.PlainText))
                sb.Append(part.PlainText);
        }

        return sb.ToString();
    }

    public static IReadOnlyList<TerminalTextWithControlPart> ToTextWithControl(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return Array.Empty<TerminalTextWithControlPart>();

        var text = input.AsSpan();
        var parts = new List<TerminalTextWithControlPart>();
        var plainBuilder = new StringBuilder(text.Length);

        void FlushPlain()
        {
            if (plainBuilder.Length == 0)
                return;

            var raw = plainBuilder.ToString();
            parts.Add(new TerminalTextWithControlPart(
                Raw: raw,
                PlainText: raw,
                IsControl: false,
                ControlType: TerminalControlType.Text));
            plainBuilder.Clear();
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];

            if (ch == '\u001b')
            {
                FlushPlain();
                parts.Add(ReadEscapePart(text, ref i));
                continue;
            }

            if (IsControlChar(ch))
            {
                FlushPlain();
                parts.Add(CreateControlCharPart(ch));
                continue;
            }

            plainBuilder.Append(ch);
        }

        FlushPlain();
        return parts;
    }

    public static bool HasControl(string? input)
    {
        if (string.IsNullOrEmpty(input))
            return false;

        var text = input.AsSpan();
        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\u001b' || IsControlChar(ch))
                return true;
        }

        return false;
    }

    private static bool IsControlChar(char ch)
    {
        return ch < ' ' || ch == '\u007f';
    }

    private static TerminalTextWithControlPart CreateControlCharPart(char ch)
    {
        var type = ch switch
        {
            '\0' => TerminalControlType.Null,
            '\a' => TerminalControlType.Bell,
            '\b' => TerminalControlType.Backspace,
            '\t' => TerminalControlType.HorizontalTab,
            '\n' => TerminalControlType.LineFeed,
            '\v' => TerminalControlType.VerticalTab,
            '\f' => TerminalControlType.FormFeed,
            '\r' => TerminalControlType.CarriageReturn,
            '\u000e' => TerminalControlType.ShiftOut,
            '\u000f' => TerminalControlType.ShiftIn,
            '\u0010' => TerminalControlType.DeviceControl1,
            '\u0011' => TerminalControlType.DeviceControl2,
            '\u0012' => TerminalControlType.DeviceControl3,
            '\u0013' => TerminalControlType.DeviceControl4,
            '\u001b' => TerminalControlType.Escape,
            '\u007f' => TerminalControlType.Delete,
            _ => TerminalControlType.C0Control
        };

        var raw = ch.ToString();
        var plain = ch is '\r' or '\n' or '\t' ? raw : string.Empty;

        return new TerminalTextWithControlPart(
            Raw: raw,
            PlainText: plain,
            IsControl: true,
            ControlType: type);
    }

    private static TerminalTextWithControlPart ReadEscapePart(ReadOnlySpan<char> text, ref int index)
    {
        var start = index;
        var i = index + 1;

        if (i >= text.Length)
        {
            return BuildControlPart(text, start, start, TerminalControlType.Escape);
        }

        var marker = text[i];

        if (marker == '[')
        {
            i++;
            while (i < text.Length && !IsCsiFinalByte(text[i]))
            {
                i++;
            }

            var end = i < text.Length ? i : text.Length - 1;
            index = end;

            var type = i < text.Length
                ? MapCsiType(text[i])
                : TerminalControlType.CsiSequence;

            return BuildControlPart(text, start, end, type);
        }

        if (marker == ']')
        {
            var end = ConsumeStringTerminatedSequence(text, i + 1);
            index = end;
            var type = MapOscType(text.Slice(start, end - start + 1));
            return BuildControlPart(text, start, end, type);
        }

        if (marker == 'P')
        {
            var end = ConsumeStringTerminatedSequence(text, i + 1);
            index = end;
            return BuildControlPart(text, start, end, TerminalControlType.DcsSequence);
        }

        if (marker is '_' or '^' or 'X')
        {
            var end = ConsumeStringTerminatedSequence(text, i + 1);
            index = end;
            var type = marker switch
            {
                '_' => TerminalControlType.ApcSequence,
                '^' => TerminalControlType.PmSequence,
                _ => TerminalControlType.SosSequence
            };
            return BuildControlPart(text, start, end, type);
        }

        if (marker is '(' or ')' or '*' or '+' or '-' or '.' or '/')
        {
            var end = Math.Min(i + 1, text.Length - 1);
            index = end;
            return BuildControlPart(text, start, end, TerminalControlType.CharsetDesignation);
        }

        index = i;
        return BuildControlPart(text, start, i, MapSingleEscapeType(marker));
    }

    private static bool IsCsiFinalByte(char ch)
    {
        return ch is >= '@' and <= '~';
    }

    private static int ConsumeStringTerminatedSequence(ReadOnlySpan<char> text, int start)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == '\a')
                return i;

            if (text[i] == '\u001b' && i + 1 < text.Length && text[i + 1] == '\\')
                return i + 1;
        }

        return text.Length - 1;
    }

    private static TerminalTextWithControlPart BuildControlPart(
        ReadOnlySpan<char> text,
        int start,
        int endInclusive,
        TerminalControlType controlType)
    {
        if (text.Length == 0)
        {
            return new TerminalTextWithControlPart(
                Raw: string.Empty,
                PlainText: string.Empty,
                IsControl: true,
                ControlType: controlType);
        }

        start = Math.Clamp(start, 0, text.Length - 1);
        endInclusive = Math.Clamp(endInclusive, start, text.Length - 1);

        var raw = new string(text.Slice(start, endInclusive - start + 1));
        return new TerminalTextWithControlPart(
            Raw: raw,
            PlainText: string.Empty,
            IsControl: true,
            ControlType: controlType);
    }

    private static TerminalControlType MapSingleEscapeType(char marker)
    {
        return marker switch
        {
            '7' => TerminalControlType.DecSaveCursor,
            '8' => TerminalControlType.DecRestoreCursor,
            'D' => TerminalControlType.Index,
            'E' => TerminalControlType.NextLine,
            'M' => TerminalControlType.ReverseIndex,
            'c' => TerminalControlType.FullReset,
            '=' => TerminalControlType.ApplicationKeypad,
            '>' => TerminalControlType.NormalKeypad,
            '\\' => TerminalControlType.StringTerminator,
            _ => TerminalControlType.Escape
        };
    }

    private static TerminalControlType MapCsiType(char finalByte)
    {
        return finalByte switch
        {
            'A' => TerminalControlType.CursorUp,
            'B' => TerminalControlType.CursorDown,
            'C' => TerminalControlType.CursorForward,
            'D' => TerminalControlType.CursorBack,
            'E' => TerminalControlType.CursorNextLine,
            'F' => TerminalControlType.CursorPreviousLine,
            'G' => TerminalControlType.CursorHorizontalAbsolute,
            'H' or 'f' => TerminalControlType.CursorPosition,
            'J' => TerminalControlType.EraseInDisplay,
            'K' => TerminalControlType.EraseInLine,
            'L' => TerminalControlType.InsertLine,
            'M' => TerminalControlType.DeleteLine,
            '@' => TerminalControlType.InsertCharacter,
            'P' => TerminalControlType.DeleteCharacter,
            'S' => TerminalControlType.ScrollUp,
            'T' => TerminalControlType.ScrollDown,
            'm' => TerminalControlType.SelectGraphicRendition,
            'n' => TerminalControlType.DeviceStatusReport,
            'h' => TerminalControlType.SetMode,
            'l' => TerminalControlType.ResetMode,
            's' => TerminalControlType.SaveCursor,
            'u' => TerminalControlType.RestoreCursor,
            'I' => TerminalControlType.FocusIn,
            'O' => TerminalControlType.FocusOut,
            _ => TerminalControlType.CsiSequence
        };
    }

    private static TerminalControlType MapOscType(ReadOnlySpan<char> rawSequence)
    {
        // raw: ESC ] {code} ; ... (BEL | ESC \)
        if (rawSequence.Length < 3)
            return TerminalControlType.OscSequence;

        var payloadStart = 2;
        var payloadEnd = rawSequence.Length;

        if (payloadEnd > 0 && rawSequence[payloadEnd - 1] == '\a')
        {
            payloadEnd -= 1;
        }
        else if (payloadEnd > 1
                 && rawSequence[payloadEnd - 2] == '\u001b'
                 && rawSequence[payloadEnd - 1] == '\\')
        {
            payloadEnd -= 2;
        }

        if (payloadEnd <= payloadStart)
            return TerminalControlType.OscSequence;

        var payload = rawSequence[payloadStart..payloadEnd];
        var separatorIndex = payload.IndexOf(';');
        var codeSpan = separatorIndex >= 0 ? payload[..separatorIndex] : payload;

        if (!int.TryParse(codeSpan, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
            return TerminalControlType.OscSequence;

        return code switch
        {
            0 => TerminalControlType.OscSetIconNameAndTitle,
            1 => TerminalControlType.OscSetIconName,
            2 => TerminalControlType.OscSetWindowTitle,
            7 => TerminalControlType.OscSetWorkingDirectory,
            8 => TerminalControlType.OscHyperlink,
            52 => TerminalControlType.OscClipboard,
            _ => TerminalControlType.OscSequence
        };
    }
}
