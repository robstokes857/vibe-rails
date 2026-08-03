using Serilog;

namespace VibeRails.Services.Terminal.Consumers;

/// <summary>
/// Writes PTY output to the console. Used by the CLI terminal path.
///
/// <para>
/// This is the <b>only</b> consumer that writes to the real OS console, so it is the only place a
/// nested-terminal concern belongs. It runs the stream through
/// <see cref="NativeConsoleOutputFilter"/> first, which drops the XTWINOPS window-geometry requests
/// that would otherwise resize the outer console window out from under the inner PTY. Every other
/// consumer — DB recording, web viewer, remote viewer — still receives the unmodified stream.
/// </para>
/// </summary>
public sealed class ConsoleOutputConsumer : ITerminalConsumer
{
    private readonly NativeConsoleOutputFilter _filter = new();
    private int _loggedDrops;

    public void OnOutput(ReadOnlyMemory<byte> data)
    {
        var filtered = _filter.Filter(data.Span);
        if (filtered.Length > 0)
            Console.Write(NativeConsoleOutputFilter.Decode(filtered));

        // Log the first drop, then at most one line per 50 more — a misbehaving TUI could otherwise
        // flood the log, and this runs once per PTY read. Compare against the last logged count
        // rather than testing for exact values: a single PTY chunk can drop several frames at once,
        // which would step straight over `== 1` and over any `% 50 == 0` milestone.
        var drops = _filter.DroppedFrameCount;
        if (_loggedDrops == 0 ? drops > 0 : drops - _loggedDrops >= 50)
        {
            _loggedDrops = drops;
            Log.Debug(
                "[Terminal] Dropped {Count} XTWINOPS window-geometry request(s) on the native console relay",
                drops);
        }
    }

    /// <summary>
    /// Flushes any partial escape sequence the filter is still holding. Without this the tail of a
    /// session — up to <see cref="NativeConsoleOutputFilter.MaxCarryBytes"/> bytes — never reaches
    /// the console.
    /// </summary>
    public void OnClosed()
    {
        var pending = _filter.Drain();
        if (pending.Length > 0)
            Console.Write(NativeConsoleOutputFilter.Decode(pending));
    }
}
