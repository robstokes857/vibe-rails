using System.Runtime.InteropServices;
using System.Text;

namespace VibeRails.Services.VCA.Hooks;

/// <summary>
/// ANSI styling palette for the interactive VCA console. Every code property returns the
/// empty string on the <see cref="Plain"/> instance, so rendering code can interpolate
/// styles unconditionally while redirected transcripts (tests, the Rules page validator,
/// VS Code's SCM log) stay byte-identical to the historical plain output.
/// </summary>
public sealed class VcaConsoleStyle
{
    public static VcaConsoleStyle Plain { get; } = new(enabled: false);
    public static VcaConsoleStyle Ansi { get; } = new(enabled: true);

    private VcaConsoleStyle(bool enabled) => Enabled = enabled;

    public bool Enabled { get; }

    public string Reset => Enabled ? "\x1b[0m" : "";
    public string Bold => Enabled ? "\x1b[1m" : "";
    public string Dim => Enabled ? "\x1b[2m" : "";
    public string Red => Enabled ? "\x1b[91m" : "";
    public string Green => Enabled ? "\x1b[92m" : "";
    public string Yellow => Enabled ? "\x1b[93m" : "";
    public string Magenta => Enabled ? "\x1b[95m" : "";
    public string Cyan => Enabled ? "\x1b[96m" : "";

    /// <summary>Erase the current line; combine with a leading carriage return.</summary>
    public string ClearLine => Enabled ? "\x1b[2K" : "";

    /// <summary>24-bit foreground color (supported by Windows Terminal / pwsh 7.5+).</summary>
    public string Fg(int r, int g, int b) => Enabled ? $"\x1b[38;2;{r};{g};{b}m" : "";

    /// <summary>24-bit background color (supported by Windows Terminal / pwsh 7.5+).</summary>
    public string Bg(int r, int g, int b) => Enabled ? $"\x1b[48;2;{r};{g};{b}m" : "";

    /// <summary>
    /// Prepares the attached console for styled output: switches it to UTF-8 (the popup
    /// child otherwise inherits the OEM codepage, which mangles ✓ into √) and enables
    /// virtual-terminal processing so ANSI escapes render on legacy conhost as well as
    /// Windows Terminal. Returns false when there is no interactive console or the
    /// console mode cannot be changed — callers must then stick to plain output.
    /// </summary>
    public static bool TryEnableForCurrentConsole()
    {
        try
        {
            if (Console.IsOutputRedirected)
            {
                return false;
            }

            Console.OutputEncoding = Encoding.UTF8;

            if (!OperatingSystem.IsWindows())
            {
                return true;
            }

            var handle = GetStdHandle(STD_OUTPUT_HANDLE);
            if (handle == IntPtr.Zero || handle == INVALID_HANDLE_VALUE)
            {
                return false;
            }

            if (!GetConsoleMode(handle, out var mode))
            {
                return false;
            }

            if ((mode & ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0)
            {
                return true;
            }

            return SetConsoleMode(handle, mode | ENABLE_VIRTUAL_TERMINAL_PROCESSING);
        }
        catch
        {
            // No console, restricted encoding, or a console mode that cannot change —
            // plain output still works everywhere, so never fail the hook over styling.
            return false;
        }
    }

    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);
}
