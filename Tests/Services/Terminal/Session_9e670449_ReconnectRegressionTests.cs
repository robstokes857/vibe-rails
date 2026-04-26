using TerminalEmulator;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

public sealed class Session_9e670449_ReconnectRegressionTests(Xunit.ITestOutputHelper output)
{
    private static readonly string BeforeRepaintFixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Services",
        "Terminal",
        "Fixtures",
        "session_9e670449_before_repaint.bin");

    private static readonly string RepaintAfterSnapshotFixturePath = Path.Combine(
        AppContext.BaseDirectory,
        "Services",
        "Terminal",
        "Fixtures",
        "session_9e670449_repaint_after_snapshot.bin");

    [Fact]
    public void SnapshotReconnect_FollowedByFrontendRefreshPolicy_DoesNotDuplicateStartupCard()
    {
        var beforeRepaintBytes = File.ReadAllBytes(BeforeRepaintFixturePath);
        var repaintBytes = File.ReadAllBytes(RepaintAfterSnapshotFixturePath);

        var server = new TerminalEmulator.Terminal(cols: 123, rows: 23, scrollbackSize: 20000);
        server.Write(beforeRepaintBytes.AsSpan());

        var reconnectSnapshot = TerminalGridSerializer.Serialize(
            server.GetScrollback(),
            server.GetSnapshot(),
            server.Rows,
            server.Cols,
            server.CursorRow,
            server.CursorCol,
            server.CursorVisible,
            server.CursorShape,
            server.IsAlternateScreen);

        var client = new TerminalEmulator.Terminal(cols: 123, rows: 23, scrollbackSize: 20000);
        client.Write(reconnectSnapshot.AsSpan());
        for (var i = 0; i < GetFrontendSyntheticPtyRepaintCopies(); i++)
            client.Write(repaintBytes.AsSpan());

        AssertStartupCardIsNotDuplicated(client);
    }

    [Fact]
    public void SnapshotReconnect_FollowedByOneCapturedPtyRedraw_DoesNotDuplicateStartupCard()
    {
        var beforeRepaintBytes = File.ReadAllBytes(BeforeRepaintFixturePath);
        var repaintBytes = File.ReadAllBytes(RepaintAfterSnapshotFixturePath);

        var server = new TerminalEmulator.Terminal(cols: 123, rows: 23, scrollbackSize: 20000);
        server.Write(beforeRepaintBytes.AsSpan());

        var reconnectSnapshot = TerminalGridSerializer.Serialize(
            server.GetScrollback(),
            server.GetSnapshot(),
            server.Rows,
            server.Cols,
            server.CursorRow,
            server.CursorCol,
            server.CursorVisible,
            server.CursorShape,
            server.IsAlternateScreen);

        var client = new TerminalEmulator.Terminal(cols: 123, rows: 23, scrollbackSize: 20000);
        client.Write(reconnectSnapshot.AsSpan());
        client.Write(repaintBytes.AsSpan());

        AssertStartupCardIsNotDuplicated(client);
    }

    [Theory]
    [InlineData("__cmd__:replay")]
    [InlineData("__replay__")]
    public void ReplayControlCommand_AcceptsStructuredAndLegacyFrames(string command)
    {
        Assert.True(TerminalControlProtocol.IsReplayCommand(command));
    }

    [Theory]
    [InlineData("__cmd__:replay:extra")]
    [InlineData("__cmd__:resize")]
    [InlineData("__replay__\r")]
    [InlineData("__cmd__:replay\r")]
    [InlineData("__cmd__:replay ")]
    [InlineData("__cmd__: replay")]
    public void ReplayControlCommand_RejectsNonReplayFrames(string command)
    {
        Assert.False(TerminalControlProtocol.IsReplayCommand(command));
    }

    private void AssertStartupCardIsNotDuplicated(TerminalEmulator.Terminal terminal)
    {
        var allRenderedLines = GetRenderedLines(terminal).ToArray();
        foreach (var line in allRenderedLines)
            output.WriteLine(line);

        Assert.InRange(CountOccurrences(allRenderedLines, "Welcome back robert!"), 0, 1);
        Assert.InRange(CountOccurrences(allRenderedLines, "Tips for getting started"), 0, 1);
        Assert.InRange(CountOccurrences(allRenderedLines, "Opus 4.7"), 0, 1);
    }

    private static int GetFrontendSyntheticPtyRepaintCopies()
    {
        var frontendPath = FindRepoFile(Path.Combine("VibeRails", "wwwroot", "js", "modules", "terminal-multitab.js"));
        var source = File.ReadAllText(frontendPath);

        var refreshBody = GetMethodSlice(source, "refreshActiveTab", "saveActiveTerminalSession");
        var reconnectBody = GetMethodSlice(source, "reconnectActiveTab", "getSelectionMeta");
        Assert.Contains("requestViewerSnapshotReplay", refreshBody);

        // triggerRedrawBump performs a temporary font-size change and restores it,
        // producing two PTY resize opportunities. In the captured Claude session,
        // replaying those real PTY redraw bytes more than once duplicates the
        // startup card in the rendered grid.
        return (ContainsSyntheticPtyRedraw(refreshBody) ? 2 : 0)
            + (ContainsSyntheticPtyRedraw(reconnectBody) ? 2 : 0);
    }

    private static bool ContainsSyntheticPtyRedraw(string methodBody)
        => methodBody.Contains("triggerRedrawBump", StringComparison.Ordinal)
            || methodBody.Contains("applyFontSize", StringComparison.Ordinal)
            || methodBody.Contains("fitAndSyncTerminal", StringComparison.Ordinal);

    private static string GetMethodSlice(string source, string methodName, string nextMethodName)
    {
        var methodStart = source.IndexOf($"    {methodName}(", StringComparison.Ordinal);
        if (methodStart < 0)
            methodStart = source.IndexOf($"    async {methodName}(", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, $"Could not find {methodName}() in terminal-multitab.js.");

        var nextMethodStart = source.IndexOf($"\n    {nextMethodName}(", methodStart, StringComparison.Ordinal);
        Assert.True(nextMethodStart > methodStart, $"Could not find the end of {methodName}() in terminal-multitab.js.");

        return source[methodStart..nextMethodStart];
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not find repository file '{relativePath}'.");
    }

    private static IEnumerable<string> GetRenderedLines(TerminalEmulator.Terminal terminal)
    {
        foreach (var row in terminal.GetScrollback())
            yield return RowToText(row);

        foreach (var line in terminal.GetScreenText())
            yield return line;
    }

    private static string RowToText(TerminalCell[] row)
    {
        var sb = new System.Text.StringBuilder(row.Length);
        foreach (var cell in row)
            cell.AppendText(sb, replaceControlWithSpace: true);
        return sb.ToString().TrimEnd();
    }

    private static int CountOccurrences(IEnumerable<string> lines, string needle)
        => lines.Sum(line => CountOccurrences(line, needle));

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
