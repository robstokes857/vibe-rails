namespace VibeRails.Services.Terminal;

/// <summary>
/// The visible cell grid of the real console hosting a native VibeRails session.
/// The inner PTY must use the same grid or full-screen TUI output will wrap at
/// different columns when it is relayed back into that console.
/// </summary>
internal readonly record struct NativeConsoleSize(int Cols, int Rows);

internal static class NativeConsoleGeometry
{
    // Keep native-console dimensions inside the same bounds accepted from xterm/relay
    // resize frames. Values outside these ranges are not useful PTY geometries.
    private const int MinCols = 10;
    private const int MaxCols = 1000;
    private const int MinRows = 5;
    private const int MaxRows = 500;

    public static bool TryGetSize(out NativeConsoleSize size)
    {
        size = default;

        try
        {
            return TryCreateSize(
                Console.IsOutputRedirected,
                Console.WindowWidth,
                Console.WindowHeight,
                out size);
        }
        catch (IOException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (PlatformNotSupportedException)
        {
            return false;
        }
    }

    internal static bool TryCreateSize(
        bool outputRedirected,
        int cols,
        int rows,
        out NativeConsoleSize size)
    {
        size = default;
        if (outputRedirected
            || cols is < MinCols or > MaxCols
            || rows is < MinRows or > MaxRows)
        {
            return false;
        }

        size = new NativeConsoleSize(cols, rows);
        return true;
    }
}
