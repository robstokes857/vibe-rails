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
    private static readonly Dictionary<string, CancellationTokenSource> s_syncResizeDeferrals = new();

    private const int ResizeRedrawDebounceMs = 160;
    private const int SyncResizePollMs = 10;
    private const int MaxSyncResizeDeferralMs = 250;

    /// <summary>
    /// When enabled, a debounced Ctrl+L is sent after resize settles.
    /// Enabled to fix incomplete ConPTY redraws on font-size change — see TERMINALS.md open bug.
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
        // Skip if PTY is already at the requested dimensions — calling ResizePseudoConsole
        // with the same size triggers a full ConPTY redraw, which produces duplicate output
        // when a replay buffer was just sent to the connecting viewer.
        if (terminal.Cols == cols && terminal.Rows == rows)
        {
            CancelSyncResizeDeferral(sessionId);
            return;
        }

        if (terminal.IsSyncOutputActive)
        {
            ScheduleDeferredResize(terminal, stateService, sessionId, cols, rows, source);
            return;
        }

        CancelSyncResizeDeferral(sessionId);
        ApplyResizeNow(terminal, stateService, sessionId, cols, rows, source);
    }

    public static void ClearSession(string sessionId)
    {
        CancellationTokenSource? redrawCts;
        CancellationTokenSource? resizeCts;
        lock (s_lock)
        {
            s_redrawDebouncers.TryGetValue(sessionId, out redrawCts);
            s_redrawDebouncers.Remove(sessionId);
            s_syncResizeDeferrals.TryGetValue(sessionId, out resizeCts);
            s_syncResizeDeferrals.Remove(sessionId);
        }

        redrawCts?.Cancel();
        redrawCts?.Dispose();
        resizeCts?.Cancel();
        resizeCts?.Dispose();
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

    private static void ApplyResizeNow(
        Terminal terminal,
        ITerminalStateService stateService,
        string sessionId,
        int cols,
        int rows,
        TerminalIoSource source)
    {
        if (terminal.Cols == cols && terminal.Rows == rows)
            return;

        terminal.Resize(cols, rows);
        stateService.RecordResize(sessionId, cols, rows, source);

        if (EnableDebouncedRedrawOnResize)
        {
            ScheduleDebouncedRedraw(terminal, sessionId);
        }
    }

    private static void ScheduleDeferredResize(
        Terminal terminal,
        ITerminalStateService stateService,
        string sessionId,
        int cols,
        int rows,
        TerminalIoSource source)
    {
        CancellationTokenSource cts;
        lock (s_lock)
        {
            if (s_syncResizeDeferrals.TryGetValue(sessionId, out var existing))
            {
                existing.Cancel();
                existing.Dispose();
            }

            cts = new CancellationTokenSource();
            s_syncResizeDeferrals[sessionId] = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var startedUtc = DateTime.UtcNow;
                while (!cts.Token.IsCancellationRequested
                    && terminal.IsSyncOutputActive
                    && (DateTime.UtcNow - startedUtc).TotalMilliseconds < MaxSyncResizeDeferralMs)
                {
                    await Task.Delay(SyncResizePollMs, cts.Token);
                }

                if (cts.Token.IsCancellationRequested)
                    return;

                if (terminal.IsSyncOutputActive)
                {
                    Log.Debug(
                        "[Terminal] Applying deferred resize during active sync-output after {DelayMs}ms fallback",
                        MaxSyncResizeDeferralMs);
                }

                ApplyResizeNow(terminal, stateService, sessionId, cols, rows, source);
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer resize or session ended.
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Terminal] Deferred resize failed");
            }
            finally
            {
                lock (s_lock)
                {
                    if (s_syncResizeDeferrals.TryGetValue(sessionId, out var current)
                        && ReferenceEquals(current, cts))
                    {
                        s_syncResizeDeferrals.Remove(sessionId);
                    }
                }

                cts.Dispose();
            }
        });
    }

    private static void CancelSyncResizeDeferral(string sessionId)
    {
        CancellationTokenSource? cts;
        lock (s_lock)
        {
            s_syncResizeDeferrals.TryGetValue(sessionId, out cts);
            s_syncResizeDeferrals.Remove(sessionId);
        }

        cts?.Cancel();
        cts?.Dispose();
    }
}
