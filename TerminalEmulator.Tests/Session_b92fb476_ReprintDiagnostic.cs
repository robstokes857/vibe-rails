using System.Text;
using TerminalEmulator;
using Xunit.v3;

namespace TerminalEmulator.Tests;

/// <summary>
/// Diagnostic for the "duplicated banner + stranded cursor" report (Rob, 2026-08-10),
/// session b92fb476-392d-4291-8952-d0cc8535842f (Claude Code v2.1.225 tab).
///
/// Rob's screenshots show (a) TWO adjacent identical Claude Code banners in scrollback
/// and (b) a stranded block cursor mid-content with the input-box chrome missing.
/// The banner is painted twice in the raw byte stream: once at ~+3.55s (inline print)
/// and once at +16.27s inside a full repaint (\e[?25l...\e[H). One mid-session resize
/// 129x26 -> 145x29 was recorded at +501.5s, ~90ms AFTER the last bytes in the stream.
///
/// This probe replays the captured SessionLogs bytes through the C# emulator to decide:
///   - do the bytes CONVERGE to one banner (bug = xterm.js live-render / client side)
///     or two banners (bytes themselves duplicate the banner = upstream Claude Code)?
///   - where does the byte-level final cursor land (stranded mid-content vs input box)?
///
/// Three variants:
///   F1: pre-resize bytes only, 129x26            (state just before the final erase burst)
///   F2: pre-resize bytes, Resize(145,29), tail   (mirrors the recorded session ordering as
///       closely as the emulator allows; note the REAL resize row landed ~90ms after the
///       tail bytes, so F3 is the truer ordering — F2 shows what a resize-then-tail replay does)
///   F3: pre-resize + tail bytes, 129x26, NO resize (control: the bytes exactly as streamed)
///
/// No hard asserts — forensic probe. Each fact skips when its git-ignored fixture is
/// missing (fixtures are exported locally from state.db, never committed). Run with:
///   dotnet test --filter "FullyQualifiedName~Session_b92fb476" -v normal
/// </summary>
public class Session_b92fb476_ReprintDiagnostic
{
    private static readonly string PreFixturePath = Path.Combine(
        AppContext.BaseDirectory, "fixtures", "session_b92fb476_pre_resize.bin");
    private static readonly string TailFixturePath = Path.Combine(
        AppContext.BaseDirectory, "fixtures", "session_b92fb476_tail.bin");

    private const string BannerNeedle = "Claude Code";
    private const string ModelNeedle = "Opus 5 (1M context)";

    private readonly ITestOutputHelper _output;

    public Session_b92fb476_ReprintDiagnostic(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void F1_PreResizeOnly_129x26_BannerCensus()
    {
        if (!File.Exists(PreFixturePath))
            Assert.Skip($"Fixture missing: {PreFixturePath}");

        var pre = File.ReadAllBytes(PreFixturePath);
        var t = new Terminal(cols: 129, rows: 26, scrollbackSize: 20000);
        t.Write(pre.AsSpan());

        _output.WriteLine("=== F1: pre_resize.bin only @ 129x26 ===");
        _output.WriteLine($"Bytes replayed: {pre.Length}");
        DumpSummary(t);
        DumpNeedleRowsWithContext(t);
    }

    [Fact]
    public void F2_PreResize_ThenResize145x29_ThenTail_FullFinalGrid()
    {
        if (!File.Exists(PreFixturePath))
            Assert.Skip($"Fixture missing: {PreFixturePath}");
        if (!File.Exists(TailFixturePath))
            Assert.Skip($"Fixture missing: {TailFixturePath}");

        var pre = File.ReadAllBytes(PreFixturePath);
        var tail = File.ReadAllBytes(TailFixturePath);

        var t = new Terminal(cols: 129, rows: 26, scrollbackSize: 20000);
        t.Write(pre.AsSpan());
        t.Resize(145, 29);
        t.Write(tail.AsSpan());

        _output.WriteLine("=== F2: pre_resize.bin @ 129x26 -> Resize(145,29) -> tail.bin ===");
        _output.WriteLine($"Bytes replayed: pre={pre.Length} tail={tail.Length}");
        DumpSummary(t);

        _output.WriteLine("=== F2 FINAL LIVE GRID (full, top -> bottom) ===");
        var snap = t.GetSnapshot();
        for (int r = 0; r < t.Rows; r++)
        {
            string marker = r == t.CursorRow ? " <-- CURSOR ROW" : "";
            _output.WriteLine($"[L{r:D2}] |{RowText(snap, r)}|{marker}");
        }
        _output.WriteLine("");
        _output.WriteLine($"Cursor row content: |{RowText(snap, Math.Clamp(t.CursorRow, 0, t.Rows - 1))}|");
        _output.WriteLine("");
        DumpNeedleRowsWithContext(t);
    }

    [Fact]
    public void F3_PreResizePlusTail_129x26_NoResize_Control()
    {
        if (!File.Exists(PreFixturePath))
            Assert.Skip($"Fixture missing: {PreFixturePath}");
        if (!File.Exists(TailFixturePath))
            Assert.Skip($"Fixture missing: {TailFixturePath}");

        var pre = File.ReadAllBytes(PreFixturePath);
        var tail = File.ReadAllBytes(TailFixturePath);

        var t = new Terminal(cols: 129, rows: 26, scrollbackSize: 20000);
        t.Write(pre.AsSpan());
        t.Write(tail.AsSpan());

        _output.WriteLine("=== F3 (control): pre_resize.bin + tail.bin @ 129x26, NO resize ===");
        _output.WriteLine($"Bytes replayed: pre={pre.Length} tail={tail.Length}");
        DumpSummary(t);

        _output.WriteLine("=== F3 FINAL LIVE GRID (full, top -> bottom) ===");
        var snap = t.GetSnapshot();
        for (int r = 0; r < t.Rows; r++)
        {
            string marker = r == t.CursorRow ? " <-- CURSOR ROW" : "";
            _output.WriteLine($"[L{r:D2}] |{RowText(snap, r)}|{marker}");
        }
        _output.WriteLine("");
        DumpNeedleRowsWithContext(t);
    }

    // ---------- helpers ----------

    private void DumpSummary(Terminal t)
    {
        var scrollback = t.GetScrollback();
        _output.WriteLine($"Live grid:       {t.Rows} rows x {t.Cols} cols");
        _output.WriteLine($"Scrollback rows: {scrollback.Length}");
        _output.WriteLine($"Cursor:          row={t.CursorRow} col={t.CursorCol}");
        _output.WriteLine($"Alt screen:      {t.IsAlternateScreen}");

        var snap = t.GetSnapshot();
        int liveBanner = 0, sbBanner = 0, liveModel = 0, sbModel = 0;
        for (int r = 0; r < t.Rows; r++)
        {
            var text = RowText(snap, r);
            if (text.Contains(BannerNeedle)) liveBanner++;
            if (text.Contains(ModelNeedle)) liveModel++;
        }
        for (int i = 0; i < scrollback.Length; i++)
        {
            var text = RowText(scrollback[i]);
            if (text.Contains(BannerNeedle)) sbBanner++;
            if (text.Contains(ModelNeedle)) sbModel++;
        }

        _output.WriteLine("=== BANNER COUNT ===");
        _output.WriteLine($"'{BannerNeedle}' live grid:  {liveBanner}");
        _output.WriteLine($"'{BannerNeedle}' scrollback: {sbBanner}");
        _output.WriteLine($"'{BannerNeedle}' total:      {liveBanner + sbBanner}");
        _output.WriteLine($"'{ModelNeedle}' live grid:  {liveModel}");
        _output.WriteLine($"'{ModelNeedle}' scrollback: {sbModel}");
        _output.WriteLine($"'{ModelNeedle}' total:      {liveModel + sbModel}");
        _output.WriteLine("");
    }

    /// <summary>
    /// Dumps every scrollback row and live-grid row containing either needle,
    /// with indices and 3 rows of context either side.
    /// </summary>
    private void DumpNeedleRowsWithContext(Terminal t)
    {
        var snap = t.GetSnapshot();
        var scrollback = t.GetScrollback();

        _output.WriteLine("=== NEEDLE ROWS + CONTEXT (scrollback, oldest -> newest) ===");
        var sbHits = new List<int>();
        for (int i = 0; i < scrollback.Length; i++)
        {
            var text = RowText(scrollback[i]);
            if (text.Contains(BannerNeedle) || text.Contains(ModelNeedle))
                sbHits.Add(i);
        }
        if (sbHits.Count == 0) _output.WriteLine("(no scrollback rows contain either needle)");
        foreach (int hit in sbHits)
        {
            _output.WriteLine($"--- scrollback hit at index {hit} ---");
            for (int i = Math.Max(0, hit - 3); i <= Math.Min(scrollback.Length - 1, hit + 3); i++)
            {
                string marker = i == hit ? " <== HIT" : "";
                _output.WriteLine($"[S{i:D3}] |{RowText(scrollback[i])}|{marker}");
            }
        }
        _output.WriteLine("");

        _output.WriteLine("=== NEEDLE ROWS + CONTEXT (live grid) ===");
        var liveHits = new List<int>();
        for (int r = 0; r < t.Rows; r++)
        {
            var text = RowText(snap, r);
            if (text.Contains(BannerNeedle) || text.Contains(ModelNeedle))
                liveHits.Add(r);
        }
        if (liveHits.Count == 0) _output.WriteLine("(no live rows contain either needle)");
        foreach (int hit in liveHits)
        {
            _output.WriteLine($"--- live hit at row {hit} ---");
            for (int r = Math.Max(0, hit - 3); r <= Math.Min(t.Rows - 1, hit + 3); r++)
            {
                string marker = r == hit ? " <== HIT" : "";
                _output.WriteLine($"[L{r:D2}] |{RowText(snap, r)}|{marker}");
            }
        }
        _output.WriteLine("");
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
