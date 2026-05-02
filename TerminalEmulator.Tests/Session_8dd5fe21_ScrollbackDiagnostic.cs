using TerminalEmulator;
using Xunit.v3;

namespace TerminalEmulator.Tests;

/// <summary>
/// Regression for session 8dd5fe21 — Rob reported "scrollback lost at end of
/// long live session in xterm.js (VS Code extension)". When the captured 30h
/// Claude Code session is replayed through the C# emulator, scrollback ends
/// up with thousands of rows including the original Claude banner and the
/// most recent VERIFICATION GATES message. So the C# server side is healthy:
/// the loss happened on the xterm.js client. The xterm.js-side fix lives in
/// vibe-terminal.js (`clearDisplay()` no longer calls `terminal.clear()`)
/// with regression coverage at UITests/tests/xterm-scrollback.spec.js. This
/// test exists to keep the server-side guarantee pinned: if the emulator ever
/// regresses and stops accumulating scrollback for main-screen TUI sessions,
/// the xterm.js fix alone will not save us — reconnect snapshots would also
/// arrive empty.
/// </summary>
public class Session_8dd5fe21_ScrollbackDiagnostic
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "fixtures", "session_8dd5fe21_full.bin");

    [Fact]
    public void Replay_PreservesScrollbackAcrossLongMainScreenTuiSession()
    {
        if (!File.Exists(FixturePath))
            Assert.Skip($"Fixture missing: {FixturePath}");

        var bytes = File.ReadAllBytes(FixturePath);
        var t = new Terminal(cols: 165, rows: 34, scrollbackSize: 20000);
        t.Write(bytes.AsSpan());

        var scrollback = t.GetScrollback();

        Assert.False(t.IsAlternateScreen);
        Assert.True(scrollback.Length > 1000,
            $"main-screen TUI session should produce substantial scrollback; got {scrollback.Length}");

        // First scrollback row pins the session start: Claude banner top.
        var first = RowText(scrollback[0]);
        Assert.Contains("Claude Code", first);
    }

    private static string RowText(TerminalCell[] row)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var cell in row)
            if (!cell.IsWideContinuation) cell.AppendText(sb, replaceControlWithSpace: true);
        return sb.ToString().TrimEnd();
    }
}
