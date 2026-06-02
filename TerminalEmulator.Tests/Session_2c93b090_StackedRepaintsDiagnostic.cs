using System.Text;
using TerminalEmulator;
using Xunit.v3;

namespace TerminalEmulator.Tests;

// Asserts the converged emulator output contains exactly ONE Claude Code banner
// (Assert.Equal at the end). Runs only when the git-ignored fixture is present
// locally; in CI the File.Exists guard skips it, so it never silently passes.
/// <summary>
/// Diagnostic for the "stacked repaints" bug (Rob, 2026-05-15). Visual debug
/// of this session in xterm.js showed three Claude Code banners stacked
/// vertically when there should be one. This test replays the captured
/// SessionLogs bytes through the C# emulator at the same dimensions the
/// session used (171×27 per the boot \e[8;27;171t report) and dumps the
/// converged grid + scrollback so we can see whether the bytes themselves
/// produce stacked banners or whether the bug lives only on the xterm.js
/// side / our snapshot serializer side.
///
/// This test does not assert behavior — it's a forensic probe. Run with:
///   dotnet test --filter "FullyQualifiedName~Session_2c93b090" -v normal
/// The grid dump is emitted via ITestOutputHelper.
/// </summary>
public class Session_2c93b090_StackedRepaintsDiagnostic
{
    private static readonly string FixturePath = Path.Combine(
        AppContext.BaseDirectory, "fixtures", "session_2c93b090_full.bin");

    private readonly ITestOutputHelper _output;

    public Session_2c93b090_StackedRepaintsDiagnostic(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Replay_DumpsConvergedGrid()
    {
        if (!File.Exists(FixturePath))
            Assert.Skip($"Fixture missing: {FixturePath}");

        var bytes = File.ReadAllBytes(FixturePath);
        var t = new Terminal(cols: 171, rows: 27, scrollbackSize: 20000);
        t.Write(bytes.AsSpan());

        var snap = t.GetSnapshot();
        var scrollback = t.GetScrollback();

        _output.WriteLine($"=== Session 2c93b090 replay ===");
        _output.WriteLine($"Bytes replayed:    {bytes.Length}");
        _output.WriteLine($"Live grid:         {t.Rows} rows x {t.Cols} cols");
        _output.WriteLine($"Scrollback rows:   {scrollback.Length}");
        _output.WriteLine($"Cursor:            row={t.CursorRow} col={t.CursorCol}");
        _output.WriteLine($"Alt screen:        {t.IsAlternateScreen}");
        _output.WriteLine("");

        _output.WriteLine("=== LIVE GRID (top → bottom) ===");
        for (int r = 0; r < t.Rows; r++)
        {
            var rowText = RowText(snap, r);
            _output.WriteLine($"[L{r:D2}] |{rowText}|");
        }
        _output.WriteLine("");

        _output.WriteLine("=== SCROLLBACK (oldest → newest) ===");
        for (int i = 0; i < scrollback.Length; i++)
        {
            var rowText = RowText(scrollback[i]);
            _output.WriteLine($"[S{i:D3}] |{rowText}|");
        }
        _output.WriteLine("");

        // Count Claude Code banner occurrences across live + scrollback.
        int liveCount = 0, sbCount = 0;
        for (int r = 0; r < t.Rows; r++)
            if (RowText(snap, r).Contains("Claude Code")) liveCount++;
        for (int i = 0; i < scrollback.Length; i++)
            if (RowText(scrollback[i]).Contains("Claude Code")) sbCount++;

        _output.WriteLine($"=== BANNER COUNT ===");
        _output.WriteLine($"'Claude Code' in live grid:   {liveCount}");
        _output.WriteLine($"'Claude Code' in scrollback:  {sbCount}");
        _output.WriteLine($"'Claude Code' total:          {liveCount + sbCount}");
        _output.WriteLine("If total > 1, the byte stream itself produces stacked banners (emulator-converged).");
        _output.WriteLine("If total == 1, bytes converge cleanly and bug lives in xterm.js or snapshot serializer.");

        // The converged emulator must show exactly one banner; >1 is the stacked-repaint
        // regression this fixture captures. Only executes when the fixture is present.
        Assert.Equal(1, liveCount + sbCount);
    }

    private static string RowText(TerminalCell[,] snap, int row)
    {
        var sb = new StringBuilder();
        for (int c = 0; c < snap.GetLength(1); c++)
        {
            var cell = snap[row, c];
            if (!cell.IsWideContinuation)
                cell.AppendText(sb, replaceControlWithSpace: true);
        }
        return sb.ToString().TrimEnd();
    }

    private static string RowText(TerminalCell[] row)
    {
        var sb = new StringBuilder();
        foreach (var cell in row)
            if (!cell.IsWideContinuation)
                cell.AppendText(sb, replaceControlWithSpace: true);
        return sb.ToString().TrimEnd();
    }
}
