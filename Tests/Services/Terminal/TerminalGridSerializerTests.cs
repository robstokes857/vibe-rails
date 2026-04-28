using System.Text;
using TerminalEmulator;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

public sealed class TerminalGridSerializerTests
{
    [Fact]
    public void SnapshotReplay_ExitsStaleViewerAlternateScreen_WhenServerIsMainScreen()
    {
        var server = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        server.Write("main screen");

        var snapshot = Serialize(server);

        var client = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        client.Write("\x1b[?1049hstale alt");
        Assert.True(client.IsAlternateScreen);

        client.Write(snapshot.AsSpan());

        Assert.False(client.IsAlternateScreen);
        Assert.Contains("main screen", client.GetScreenText()[0]);
    }

    [Fact]
    public void SnapshotReplay_EntersAlternateScreen_WhenServerIsAlternateScreen()
    {
        var server = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        server.Write("\x1b[?1049hactive alt");

        var snapshot = Serialize(server);

        var client = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        client.Write(snapshot.AsSpan());

        Assert.True(client.IsAlternateScreen);
        Assert.Contains("active alt", client.GetScreenText()[0]);
    }

    [Fact]
    public void SnapshotReplay_PrologueResetsKnownTransientModes()
    {
        var server = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        server.Write("ready");

        var replay = Encoding.UTF8.GetString(Serialize(server));

        Assert.Contains("\x1b[?1049;1047;47l", replay);
        Assert.Contains("2004;2026l", replay);
        Assert.Contains("\x1b[r", replay);
        Assert.Contains("\x1b>", replay);
    }

    private static byte[] Serialize(TerminalEmulator.Terminal terminal)
        => TerminalGridSerializer.Serialize(
            terminal.GetScrollback(),
            terminal.GetSnapshot(),
            terminal.Rows,
            terminal.Cols,
            terminal.CursorRow,
            terminal.CursorCol,
            terminal.CursorVisible,
            terminal.CursorShape,
            terminal.IsAlternateScreen);
}
