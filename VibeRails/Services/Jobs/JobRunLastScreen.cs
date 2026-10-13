using System.Text;
using Serilog;
using TerminalEmulator;
using VibeRails.Data.Replay;
using VibeRails.DTOs;
using VibeRails.Security;
using EmulatorTerminal = TerminalEmulator.Terminal;

namespace VibeRails.Services.Jobs;

/// <summary>
/// Why a run stopped, read off its recording. A Worker that fails says why on its own screen
/// ("Error: ... unauthorized (401)") while the run learns only an exit code, and a reaped run
/// learns nothing at all. Replaying the tail of the recording through the emulator the live
/// terminal uses rebuilds the screen as it looked; stripping escapes cannot, because a TUI
/// draws with cursor movement rather than in reading order.
/// </summary>
internal static class JobRunLastScreen
{
    /// <summary>
    /// Enough recording to rebuild the final screen, which a full-screen CLI repaints in far
    /// less. A whole recording can run to megabytes, and opening a run must not replay that.
    /// </summary>
    internal const int TailBytes = 256 * 1024;

    internal const int MaxLines = 30;

    internal const int MaxFinalLineCharacters = 300;

    private const int ScrollbackRows = MaxLines * 4;

    /// <summary>Fills <see cref="JobRunResponse.LastScreen"/> for a run that ended without succeeding.</summary>
    public static JobRunResponse Attach(JobRunResponse run, IReplayStore replay)
    {
        if (run.Status is not (JobRunStatus.Failed or JobRunStatus.Cancelled
            or JobRunStatus.TimedOut or JobRunStatus.Interrupted))
            return run;

        var sessionId = run.TerminalSessionId ?? run.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
            return run;

        var screen = TryRender(replay, sessionId);
        return screen is null ? run : run with { LastScreen = Tail(screen) };
    }

    /// <summary>
    /// The last line the CLI printed before exiting, rejoined where the terminal wrapped it.
    /// Null when the recording ended inside a full-screen TUI, whose bottom row is its own
    /// chrome ("? for shortcuts") rather than anything it said on the way out.
    /// </summary>
    public static string? FinalLine(IReplayStore? replay, string? sessionId)
    {
        if (replay is null || string.IsNullOrWhiteSpace(sessionId))
            return null;

        var screen = TryRender(replay, sessionId);
        return screen is null ? null : FinalLine(screen);
    }

    internal sealed record RenderedScreen(IReadOnlyList<string> Lines, int Cols, bool EndedOnAlternateScreen);

    internal static RenderedScreen Render(SessionTail tail)
    {
        if (tail.Frames.Count == 0)
            return new([], 0, false);

        var first = tail.Frames[0];
        var terminal = new EmulatorTerminal(Cols(first.Cols), Rows(first.Rows), ScrollbackRows);
        // The tail can begin inside a TUI that switched screens before it. Without this its
        // repaints would scroll into the main screen's history as if they had been printed.
        if (tail.StartsOnAlternateScreen)
            terminal.Write("\x1b[?1049h"u8);

        foreach (var frame in tail.Frames)
        {
            int cols = Cols(frame.Cols), rows = Rows(frame.Rows);
            if (cols != terminal.Cols || rows != terminal.Rows)
                terminal.Resize(cols, rows);
            terminal.Write(frame.Data);
        }

        var lines = new List<string>();
        // The alternate screen has no history of its own, only the frame it shows.
        if (!terminal.IsAlternateScreen)
            lines.AddRange(terminal.GetScrollback().Select(RowText));
        lines.AddRange(terminal.GetScreenText());
        return new(lines, terminal.Cols, terminal.IsAlternateScreen);
    }

    /// <summary>The bottom of the screen, newest last, with runs of blank rows collapsed to one.</summary>
    internal static List<string> Tail(RenderedScreen screen, int maxLines = MaxLines)
    {
        var tail = new List<string>();
        for (var index = screen.Lines.Count - 1; index >= 0 && tail.Count < maxLines; index--)
        {
            var line = screen.Lines[index];
            if (string.IsNullOrWhiteSpace(line))
            {
                // A TUI's empty middle is dozens of blank rows; one keeps the paragraph break.
                if (tail.Count > 0 && tail[^1].Length > 0)
                    tail.Add(string.Empty);
                continue;
            }
            tail.Add(line);
        }

        if (tail.Count > 0 && tail[^1].Length == 0)
            tail.RemoveAt(tail.Count - 1);
        tail.Reverse();
        return tail;
    }

    internal static string? FinalLine(RenderedScreen screen)
    {
        if (screen.EndedOnAlternateScreen)
            return null;

        var last = screen.Lines.Count - 1;
        while (true)
        {
            // Skip rules, box corners and spinners: a line with nothing to read says nothing.
            while (last >= 0 && !screen.Lines[last].Any(char.IsLetterOrDigit))
                last--;
            if (last < 0)
                return null;

            // A row filled to the final column continued on the next one.
            var first = last;
            while (first > 0 && last - first < 8 && screen.Lines[first - 1].Length >= screen.Cols)
                first--;

            var text = string.Concat(screen.Lines.Skip(first).Take(last - first + 1)).Trim();
            if (IsExitFooter(text))
            {
                last = first - 1;
                continue;
            }

            // The reason is stored on the run and served beyond the root backend (run lists,
            // card status, read_board_check), so it gets the same masking as outbound text.
            text = CredentialRedactor.RedactTranscript(text);
            return text.Length <= MaxFinalLineCharacters
                ? text
                : string.Concat(text.AsSpan(0, MaxFinalLineCharacters - 1), "…");
        }
    }

    // An inline CLI's own goodbye says nothing about why it failed; the line above it usually does.
    private static readonly string[] ExitFooters =
        ["To continue this session", "Token usage:", "codex resume ", "claude --resume ", "Resume this session"];

    private static bool IsExitFooter(string line) =>
        ExitFooters.Any(footer => line.StartsWith(footer, StringComparison.OrdinalIgnoreCase));

    private static RenderedScreen? TryRender(IReplayStore replay, string sessionId)
    {
        try
        {
            return Render(replay.Tail(sessionId, TailBytes));
        }
        catch (Exception ex)
        {
            // The screen explains a failure; failing to read it must not become one.
            Log.Warning(ex, "[Jobs] Could not read the last screen of session {SessionId}", sessionId);
            return null;
        }
    }

    private static string RowText(TerminalCell[] row)
    {
        var builder = new StringBuilder(row.Length);
        foreach (var cell in row)
            cell.AppendText(builder, replaceControlWithSpace: true);
        return builder.ToString().TrimEnd();
    }

    // Recorded geometry is trusted for layout but not for allocation size.
    private static int Cols(int cols) => cols is > 0 and <= 1000 ? cols : 120;

    private static int Rows(int rows) => rows is > 0 and <= 500 ? rows : 30;
}
