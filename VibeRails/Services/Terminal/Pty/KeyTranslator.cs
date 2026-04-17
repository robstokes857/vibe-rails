namespace VibeRails.Services.Terminal;

/// <summary>
/// Translates Console.ReadKey results to ANSI escape sequences for PTY input.
/// Used by the CLI terminal path for console-based I/O.
/// </summary>
public static class KeyTranslator
{
    public static string TranslateKey(ConsoleKeyInfo key)
    {
        return key.Key switch
        {
            ConsoleKey.UpArrow => "\x1B[A",
            ConsoleKey.DownArrow => "\x1B[B",
            ConsoleKey.RightArrow => "\x1B[C",
            ConsoleKey.LeftArrow => "\x1B[D",
            ConsoleKey.Home => "\x1B[H",
            ConsoleKey.End => "\x1B[F",
            ConsoleKey.Delete => "\x1B[3~",
            ConsoleKey.Insert => "\x1B[2~",
            ConsoleKey.PageUp => "\x1B[5~",
            ConsoleKey.PageDown => "\x1B[6~",
            ConsoleKey.F1 => "\x1BOP",
            ConsoleKey.F2 => "\x1BOQ",
            ConsoleKey.F3 => "\x1BOR",
            ConsoleKey.F4 => "\x1BOS",
            ConsoleKey.F5 => "\x1B[15~",
            ConsoleKey.F6 => "\x1B[17~",
            ConsoleKey.F7 => "\x1B[18~",
            ConsoleKey.F8 => "\x1B[19~",
            ConsoleKey.F9 => "\x1B[20~",
            ConsoleKey.F10 => "\x1B[21~",
            ConsoleKey.F11 => "\x1B[23~",
            ConsoleKey.F12 => "\x1B[24~",
            ConsoleKey.Tab => "\t",
            ConsoleKey.Enter => "\r",
            ConsoleKey.Backspace => "\x7F",
            ConsoleKey.Escape => "\x1B",
            _ => key.KeyChar == '\0' ? string.Empty : key.KeyChar.ToString()
        };
    }
}
