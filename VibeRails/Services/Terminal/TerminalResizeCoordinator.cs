using Serilog;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Centralizes PTY resize handling so resize hooks and optional redraw behavior
/// are consistent across local and remote viewer paths.
/// </summary>
internal static class TerminalResizeCoordinator
{
    private static readonly Lock s_lock = new();
    private static readonly Dictionary<string, CancellationTokenSource> s_redrawDebouncers = new();

    private const int ResizeRedrawDebounceMs = 160;

    /// <summary>
    /// Default is off. When enabled, a debounced Ctrl+L is sent after resize settles.
    /// </summary>
    public static bool EnableDebouncedRedrawOnResize { get; set; } = false;

    public static void ApplyResize(
        Terminal terminal,
        ITerminalStateService stateService,
        string sessionId,
        int cols,
        int rows,
        TerminalIoSource source)
    {
        terminal.Resize(cols, rows);
        stateService.RecordResize(sessionId, cols, rows, source);

        if (EnableDebouncedRedrawOnResize)
        {
            ScheduleDebouncedRedraw(terminal, sessionId);
        }
    }

    public static void ClearSession(string sessionId)
    {
        CancellationTokenSource? cts;
        lock (s_lock)
        {
            s_redrawDebouncers.TryGetValue(sessionId, out cts);
            s_redrawDebouncers.Remove(sessionId);
        }

        if (cts == null)
            return;

        cts.Cancel();
        cts.Dispose();
    }

    private static void ScheduleDebouncedRedraw(Terminal terminal, string sessionId)
    {
        CancellationTokenSource cts;
        lock (s_lock)
        {
            if (s_redrawDebouncers.TryGetValue(sessionId, out var existing))
            {
                existing.Cancel();
                existing.Dispose();
            }

            cts = new CancellationTokenSource();
            s_redrawDebouncers[sessionId] = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ResizeRedrawDebounceMs, cts.Token);
                if (!cts.Token.IsCancellationRequested)
                {
                    await terminal.WriteBytesAsync(new byte[] { 0x0C }, cts.Token); // Ctrl+L
                }
            }
            catch (OperationCanceledException)
            {
                // Superseded by a new resize or session ended.
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Terminal] Debounced redraw on resize failed");
            }
            finally
            {
                lock (s_lock)
                {
                    if (s_redrawDebouncers.TryGetValue(sessionId, out var current) && ReferenceEquals(current, cts))
                    {
                        s_redrawDebouncers.Remove(sessionId);
                    }
                }

                cts.Dispose();
            }
        });
    }
}
