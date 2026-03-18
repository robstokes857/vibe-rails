using System.Text;
using TerminalEmulator;

namespace TerminalEmulator.Tests;

/// <summary>
/// Golden-file tests: replay a .bin fixture and compare the final rendered screen
/// against a committed .txt snapshot. Any regression in the ANSI parser or buffer
/// logic that changes visible output will be caught here.
///
/// To regenerate snapshots after an intentional change:
///   dotnet run --project IntegrationTest -- snapshot
/// Then commit the updated .txt files.
/// </summary>
public class SnapshotTests
{
    private static readonly string FixturesDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures"));

    private static readonly string SnapshotsDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "snapshots"));

    private static bool HasData =>
        Directory.Exists(FixturesDir) && Directory.Exists(SnapshotsDir);

    // ------------------------------------------------------------------
    // Golden screen text comparison
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(GetSnapshotPairs))]
    public void FinalScreen_MatchesSnapshot(string fixtureFile, string snapshotFile)
    {
        if (!HasData) Assert.Skip("Run 'export' then 'snapshot' commands first.");

        var (expectedLines, expectedCols, expectedRows) = ReadSnapshot(snapshotFile);

        var terminal = new Terminal(cols: expectedCols, rows: expectedRows);
        terminal.Write(File.ReadAllBytes(fixtureFile).AsSpan());

        var actualLines = terminal.GetScreenText();

        Assert.Equal(expectedLines.Length, actualLines.Length);
        for (int r = 0; r < expectedLines.Length; r++)
            Assert.Equal(expectedLines[r], NormalizeSurrogates(actualLines[r]));
    }

    // ------------------------------------------------------------------
    // Cursor position is preserved across replay
    // ------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(GetSnapshotPairs))]
    public void CursorPosition_MatchesSnapshot(string fixtureFile, string snapshotFile)
    {
        if (!HasData) Assert.Skip("Run 'export' then 'snapshot' commands first.");

        var (_, expectedCols, expectedRows, expectedCursorCol, expectedCursorRow) = ReadSnapshotFull(snapshotFile);

        var terminal = new Terminal(cols: expectedCols, rows: expectedRows);
        terminal.Write(File.ReadAllBytes(fixtureFile).AsSpan());

        Assert.Equal(expectedCursorCol, terminal.CursorCol);
        Assert.Equal(expectedCursorRow, terminal.CursorRow);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    public static IEnumerable<object[]> GetSnapshotPairs()
    {
        if (!Directory.Exists(SnapshotsDir) || !Directory.Exists(FixturesDir))
            return [["(no snapshots)", "(no snapshots)"]];

        var pairs = new List<object[]>();
        foreach (var snapshotFile in Directory.GetFiles(SnapshotsDir, "*.txt"))
        {
            string sessionId = Path.GetFileNameWithoutExtension(snapshotFile);
            string fixtureFile = Path.Combine(FixturesDir, $"{sessionId}.bin");
            if (File.Exists(fixtureFile))
                pairs.Add([fixtureFile, snapshotFile]);
        }

        return pairs.Count > 0 ? pairs : [["(no snapshots)", "(no snapshots)"]];
    }

    // Unpaired surrogates can't round-trip through UTF-8; normalize to replacement char.
    private static string NormalizeSurrogates(string s)
    {
        if (!s.Any(char.IsSurrogate)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                sb.Append('\uFFFD');
                i++;
                continue;
            }

            sb.Append(char.IsSurrogate(c) ? '\uFFFD' : c);
        }
        return sb.ToString();
    }

    private static (string[] Lines, int Cols, int Rows) ReadSnapshot(string path)
    {
        var full = ReadSnapshotFull(path);
        return (full.Lines, full.Cols, full.Rows);
    }

    private static (string[] Lines, int Cols, int Rows, int CursorCol, int CursorRow) ReadSnapshotFull(string path)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        int cols = 80, rows = 24, cursorCol = 0, cursorRow = 0;
        int contentStart = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i] == "---") { contentStart = i + 1; break; }
            if (lines[i].StartsWith("COLS="))   cols      = int.Parse(lines[i][5..]);
            if (lines[i].StartsWith("ROWS="))   rows      = int.Parse(lines[i][5..]);
            if (lines[i].StartsWith("CURSOR="))
            {
                var parts = lines[i][7..].Split(',');
                cursorCol = int.Parse(parts[0]);
                cursorRow = int.Parse(parts[1]);
            }
        }

        // GetScreenText() always returns exactly `rows` lines; take exactly that many.
        var content = lines.Length >= contentStart + rows
            ? lines[contentStart..(contentStart + rows)]
            : lines[contentStart..];

        return (content, cols, rows, cursorCol, cursorRow);
    }
}
