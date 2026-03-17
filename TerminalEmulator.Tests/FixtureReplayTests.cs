using TerminalEmulator;

namespace TerminalEmulator.Tests;

/// <summary>
/// Replay tests using real PTY captures exported from state.db.
/// Run FixtureExporter first to generate the .bin files, then these tests replay them.
///
/// Tests are marked as Skip when fixture directory doesn't exist so CI doesn't fail
/// on a clean checkout. Once fixtures are generated they run automatically.
/// </summary>
public class FixtureReplayTests
{
    private static readonly string FixturesDir =
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures");

    private static bool FixturesExist =>
        Directory.Exists(FixturesDir) &&
        Directory.GetFiles(FixturesDir, "*.bin").Length > 0;

    // ------------------------------------------------------------------
    // Smoke tests — feed entire session, assert no crash + valid state
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(GetFixtureFiles))]
    public void Replay_FullSession_NoException(string fixturePath)
    {
        if (!FixturesExist) Assert.Skip("Run FixtureExporter to generate fixtures first.");

        var bytes = File.ReadAllBytes(fixturePath);
        var t = new Terminal(cols: 220, rows: 50);

        var exception = Record.Exception(() => t.Write(bytes.AsSpan()));

        Assert.Null(exception);
        Assert.InRange(t.CursorCol, 0, t.Cols); // Cols is valid: pending-wrap state after last column
        Assert.InRange(t.CursorRow, 0, t.Rows - 1);
    }

    [Theory]
    [MemberData(nameof(GetFixtureFiles))]
    public void Replay_FullSession_GridCellsValid(string fixturePath)
    {
        if (!FixturesExist) Assert.Skip("Run FixtureExporter to generate fixtures first.");

        var bytes = File.ReadAllBytes(fixturePath);
        var t = new Terminal(cols: 220, rows: 50);
        t.Write(bytes.AsSpan());

        var snap = t.GetSnapshot();
        for (int r = 0; r < t.Rows; r++)
        for (int c = 0; c < t.Cols; c++)
        {
            var cell = snap[r, c];
            // No null chars in non-continuation cells
            if (!cell.IsWideContinuation)
                Assert.True(cell.Char >= ' ' || cell.Char == '\0',
                    $"Unexpected control char 0x{(int)cell.Char:X2} at [{r},{c}]");
        }
    }

    // ------------------------------------------------------------------
    // Chunk-by-chunk replay — simulates real PTY stream behaviour
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(GetFixtureFiles))]
    public void Replay_ChunkByChunk_MatchesFullReplay(string fixturePath)
    {
        if (!FixturesExist) Assert.Skip("Run FixtureExporter to generate fixtures first.");

        var bytes = File.ReadAllBytes(fixturePath);

        // Full replay
        var full = new Terminal(cols: 220, rows: 50);
        full.Write(bytes.AsSpan());
        var fullSnap = full.GetSnapshot();

        // Chunk replay — simulate PTY arriving in small pieces
        var chunked = new Terminal(cols: 220, rows: 50);
        const int chunkSize = 64;
        for (int i = 0; i < bytes.Length; i += chunkSize)
        {
            int len = Math.Min(chunkSize, bytes.Length - i);
            chunked.Write(bytes.AsSpan(i, len));
        }
        var chunkSnap = chunked.GetSnapshot();

        // Grids must match
        for (int r = 0; r < full.Rows; r++)
        for (int c = 0; c < full.Cols; c++)
        {
            Assert.Equal(fullSnap[r, c].Char, chunkSnap[r, c].Char);
        }
    }

    // ------------------------------------------------------------------
    // Performance test
    // ------------------------------------------------------------------

    [Fact]
    public void Replay_LargestSession_CompletesUnder5Seconds()
    {
        if (!FixturesExist) Assert.Skip("Run FixtureExporter to generate fixtures first.");

        var files = Directory.GetFiles(FixturesDir, "*.bin")
            .OrderByDescending(f => new FileInfo(f).Length)
            .FirstOrDefault();

        if (files is null) Assert.Skip("No fixture files found.");

        var bytes = File.ReadAllBytes(files!);
        var t = new Terminal(cols: 220, rows: 50);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        t.Write(bytes.AsSpan());
        sw.Stop();

        var info = new FileInfo(files!);
        Console.WriteLine($"Parsed {info.Length / 1024} KB in {sw.ElapsedMilliseconds} ms " +
                          $"({info.Length / 1024.0 / sw.Elapsed.TotalSeconds:F0} KB/s)");

        Assert.True(sw.Elapsed.TotalSeconds < 5,
            $"Parsing took {sw.ElapsedMilliseconds} ms — expected < 5000 ms");
    }

    // ------------------------------------------------------------------
    // Dirty row tracking through replay
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(GetFixtureFiles))]
    public void Replay_OnRenderFires_WithValidRowIndices(string fixturePath)
    {
        if (!FixturesExist) Assert.Skip("Run FixtureExporter to generate fixtures first.");

        var bytes = File.ReadAllBytes(fixturePath);
        var t = new Terminal(cols: 220, rows: 50);

        t.OnRender += dirtyRows =>
        {
            foreach (var row in dirtyRows)
                Assert.InRange(row, 0, t.Rows - 1);
        };

        t.Write(bytes.AsSpan());
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> GetFixtureFiles()
    {
        if (!Directory.Exists(FixturesDir))
            return [["(no fixtures)"]];

        var files = Directory.GetFiles(FixturesDir, "*.bin");
        if (files.Length == 0)
            return [["(no fixtures)"]];

        return files.Select(f => new object[] { f });
    }
}
