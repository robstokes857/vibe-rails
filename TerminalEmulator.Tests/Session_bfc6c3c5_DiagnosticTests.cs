using TerminalEmulator;
using Xunit.v3;

namespace TerminalEmulator.Tests;

/// <summary>
/// Regression for session bfc6c3c5-43ed-4c1d-a3d9-3a4147246e61 — Rob reported the
/// Claude Code splash "double-printed" at session start. Replaying the captured
/// PTY bytes through the emulator at the session's reported geometry (171×27)
/// reproduces the user-visible artifact: row 3 contained
/// <c>" Claude Code╭─── Claude Code v2.1.119 ───╮"</c> instead of just the banner.
/// The first <c>" Claude Code"</c> was the OSC-title payload of
/// <c>\e]0;✳ Claude Code\a</c> leaking onto the screen because the parser
/// mis-terminated the OSC on the 0x9C continuation byte of <c>✳</c>'s UTF-8
/// encoding. See <see cref="OscUtf8PayloadTests"/> for the parser-level
/// regression. This integration test pins the end-to-end behavior on the
/// actual captured bytes.
/// </summary>
public class Session_bfc6c3c5_DiagnosticTests(ITestOutputHelper output)
{
    private static readonly string FixturePath = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "session_bfc6c3c5_startup.bin"));

    [Fact]
    public void Replay_BannerStartsCleanly_NoOscTitleLeak()
    {
        if (!File.Exists(FixturePath))
            Assert.Skip($"Fixture not found: {FixturePath}");

        var bytes = File.ReadAllBytes(FixturePath);
        var t = new Terminal(cols: 171, rows: 27);
        t.Write(bytes.AsSpan());

        var lines = t.GetScreenText();
        for (int r = 0; r < lines.Length; r++)
            output.WriteLine($"{r:D2}: {lines[r]}");

        // Row 3 is where Claude Code's banner top-border lands (rows 0-2 are
        // PowerShell init + the echoed `claude` command). It must begin with
        // the corner glyph, with no leaked OSC text in front.
        Assert.StartsWith("╭───", lines[3].TrimStart());
        Assert.DoesNotContain("Claude Code╭", lines[3]);

        // Splash banner appears exactly once on screen.
        Assert.Equal(1, lines.Count(l => l.Contains('╭')));
        Assert.Equal(1, lines.Count(l => l.Contains("Welcome back robert!")));
    }
}
