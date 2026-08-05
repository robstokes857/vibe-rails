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

    // opentui's exact terminal setup, as captured at the start of every OpenCode /
    // GLM 5.2 session (session 71dee36a chunk 26179241). Emitted once and
    // never re-asserted — not even on SIGWINCH.
    private const string OpenTuiStartupModes = "\x1b[?1000h\x1b[?1002h\x1b[?1003h\x1b[?1006h";

    [Fact]
    public void SnapshotReplay_RestoresMouseTrackingModes_WhenServerHasThemActive()
    {
        // Same failure shape as bracketed paste above. If the snapshot resets mouse
        // tracking without restoring it, xterm.js has no active mouse protocol and
        // falls back to alt-scroll: wheel events become cursor-up/down, which
        // OpenCode's composer reads as input-history navigation. Sticky for the rest
        // of the session, because opentui never re-asserts. See runbooks/terminal/
        // TERMINAL.md "## 2026-07-26 GLM 5.2 wheel acts like a held up-arrow".
        var server = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        server.Write("\x1b[?1049h" + OpenTuiStartupModes);
        // ?1000/?1002/?1003 are last-wins, so the effective protocol is 1003 (any-event).
        Assert.Equal([1003, 1006], server.GetInputReportingModes());

        var snapshot = Serialize(server);

        var client = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        client.Write(snapshot.AsSpan());

        Assert.Equal([1003, 1006], client.GetInputReportingModes());
    }

    [Fact]
    public void SnapshotReplay_RestoresMouseModesInOneDecset_AfterThePrologueResetsThem()
    {
        var server = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        server.Write(OpenTuiStartupModes + "\x1b[?1004h");

        var replay = Encoding.UTF8.GetString(Serialize(server));

        // Protocol, then encoding, then focus.
        Assert.Contains("\x1b[?1003;1006;1004h", replay);
        // ...and it must land after the blanket reset, or it's undone immediately.
        Assert.True(
            replay.IndexOf("1000;1002;1003;1004;1005;1006;1007;1015;2004;2026l", StringComparison.Ordinal)
                < replay.IndexOf("\x1b[?1003;1006;1004h", StringComparison.Ordinal),
            "mouse-mode restore must come after the reset prologue");
    }

    [Fact]
    public void SnapshotReplay_RestoresTheProtocolTheAppLastAskedFor_NotAnUpgradedOne()
    {
        // The bug this pins: modelling 1000/1002/1003 as an accumulating set and
        // replaying them sorted would emit "…1002;1003h", leaving xterm.js on
        // any-event tracking when the app actually asked for drag. xterm.js's
        // activeProtocol is a single last-wins value, so the replay must be too.
        var server = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        server.Write("\x1b[?1003h\x1b[?1002h\x1b[?1006h");
        Assert.Equal([1002, 1006], server.GetInputReportingModes());

        var replay = Encoding.UTF8.GetString(Serialize(server));

        Assert.Contains("\x1b[?1002;1006h", replay);
        Assert.DoesNotContain("1003;1006h", replay);
    }

    // The exact reset prologue the serializer emits. Asserted verbatim (not by fragment)
    // so a serializer change breaks a test instead of silently invalidating the
    // "nothing after the prologue" assertions below.
    private const string PrologueReset = "\x1b[?1;6;9;1000;1002;1003;1004;1005;1006;1007;1015;2004;2026l";

    [Fact]
    public void SnapshotReplay_LeavesMouseModesOff_WhenServerNeverEnabledThem()
    {
        var server = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        server.Write("plain shell");
        Assert.Empty(server.GetInputReportingModes());

        var replay = Encoding.UTF8.GetString(Serialize(server));

        var resetIndex = replay.IndexOf(PrologueReset, StringComparison.Ordinal);
        Assert.True(resetIndex >= 0, "snapshot must contain the reset prologue");
        // Nothing after the prologue may re-enable any input reporting mode.
        var afterPrologue = replay[(resetIndex + PrologueReset.Length)..];
        Assert.DoesNotContain("\x1b[?9", afterPrologue);
        Assert.DoesNotContain("\x1b[?100", afterPrologue);
    }

    [Fact]
    public void Emulator_TracksMouseModes_OnSetAndReset()
    {
        var t = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        Assert.Empty(t.GetInputReportingModes());

        t.Write(OpenTuiStartupModes);
        Assert.Equal([1003, 1006], t.GetInputReportingModes());

        // A viewer's snapshot prologue disables them; the emulator must follow.
        t.Write(PrologueReset);
        Assert.Empty(t.GetInputReportingModes());
    }

    [Fact]
    public void Emulator_DisablesTrackingEntirely_WhenAnySingleProtocolModeIsReset()
    {
        // xterm.js maps DECRST of 9/1000/1002/1003 to activeProtocol = NONE, so a
        // partial reset must not leave an earlier mode standing.
        var t = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        t.Write(OpenTuiStartupModes);
        Assert.Equal([1003, 1006], t.GetInputReportingModes());

        t.Write("\x1b[?1000l");

        Assert.Equal([1006], t.GetInputReportingModes());
    }

    [Fact]
    public void Emulator_ClearsSnapshotRestoredModes_OnRis()
    {
        var t = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        t.Write(OpenTuiStartupModes + "\x1b[?1004h\x1b[?2004h");
        Assert.NotEmpty(t.GetInputReportingModes());
        Assert.True(t.BracketedPasteActive);

        // RIS = ESC + 'c'. Written with a \u001b escape because C#'s \x
        // escape is greedy: it would eat the 'c' as a fourth hex digit (U+01BC).
        t.Write("\u001bc");

        // Otherwise a post-reset snapshot re-enables reporting the app no longer
        // has on — wheel ticks then type SGR reports into a plain shell's stdin.
        Assert.Empty(t.GetInputReportingModes());
        Assert.False(t.BracketedPasteActive);
    }

    [Fact]
    public void Emulator_ClearsSnapshotRestoredModes_OnPublicReset()
    {
        var t = new TerminalEmulator.Terminal(cols: 20, rows: 5, scrollbackSize: 100);
        t.Write(OpenTuiStartupModes + "\x1b[?2004h");

        t.Reset();

        Assert.Empty(t.GetInputReportingModes());
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
            terminal.BracketedPasteActive,
            terminal.GetInputReportingModes());
}
