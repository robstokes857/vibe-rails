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

    [Fact]
    public void SnapshotReplay_RestoresBracketedPasteMode_WhenServerHasItActive()
    {
        // A CLI (e.g. Claude Code) enables bracketed paste once at startup and never
        // re-sends it. A reconnecting viewer resets its modes and relies on the
        // snapshot to restore them — so the snapshot must re-enable ?2004h, or the
        // webview's Shift+Enter→newline gate (which reads xterm's bracketed-paste
        // mode) silently breaks for that tab.
        var server = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        server.Write("\x1b[?2004hprompt");
        Assert.True(server.BracketedPasteActive);

        var snapshot = Serialize(server);

        var client = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        client.Write(snapshot.AsSpan());

        Assert.True(client.BracketedPasteActive);
    }

    [Fact]
    public void SnapshotReplay_LeavesBracketedPasteOff_WhenServerInactive()
    {
        var server = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        server.Write("plain shell");
        Assert.False(server.BracketedPasteActive);

        var replay = Encoding.UTF8.GetString(Serialize(server));

        // Prologue still disables it; nothing re-enables it.
        Assert.Contains("2004;2026l", replay);
        Assert.DoesNotContain("\x1b[?2004h", replay);
    }

    [Fact]
    public void Emulator_TracksBracketedPasteMode_OnSetAndReset()
    {
        var t = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        Assert.False(t.BracketedPasteActive);

        t.Write("\x1b[?2004h");
        Assert.True(t.BracketedPasteActive);

        t.Write("\x1b[?2004l");
        Assert.False(t.BracketedPasteActive);
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
            terminal.IsAlternateScreen,
            terminal.BracketedPasteActive);
}
