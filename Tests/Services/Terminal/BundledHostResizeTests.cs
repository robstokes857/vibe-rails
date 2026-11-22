using System.Text;
using Xunit;
using Emulator = TerminalEmulator.Terminal;
using TerminalPty = VibeRails.Services.Terminal.Terminal;

namespace Tests.Services.Terminal;

/// <summary>
/// VIBE-354: a bundled console host repaints nothing after a resize, so the server's emulator has
/// to come out of one holding what the viewer shows. When a terminal gets shorter xterm.js keeps
/// the line the cursor is on and lets the top rows scroll into history
/// (UITests/tests/xterm-device-queries.spec.js, "a shorter terminal keeps the line the cursor is
/// on", the same nine lines and a prompt). The emulator's own resize keeps the top rows instead.
/// </summary>
public sealed class BundledHostResizeTests
{
    [Fact]
    public void AShorterTerminalKeepsTheLineTheCursorIsOnAndItsTopRowsBecomeHistory()
    {
        var emulator = NineLinesAndAPrompt();

        TerminalPty.KeepCursorLineOnScreen(emulator, newRows: 6);
        emulator.Resize(40, 6);

        Assert.Equal((5, 7), (emulator.CursorRow, emulator.CursorCol));
        Assert.Equal(["line 5", "line 6", "line 7", "line 8", "line 9", "prompt>"], emulator.GetScreenText());
        Assert.Equal(["line 1", "line 2", "line 3", "line 4"], History(emulator));
    }

    [Fact]
    public void TheEmulatorsOwnResizeStillDropsTheBottomRows()
    {
        // The reason KeepCursorLineOnScreen exists. If this fails, TerminalBuffer.Resize has
        // learned to keep the cursor's line and the helper can go (VB-G9STX-427, part B).
        var emulator = NineLinesAndAPrompt();

        emulator.Resize(40, 6);

        Assert.Equal(["line 1", "line 2", "line 3", "line 4", "line 5", "line 6"], emulator.GetScreenText());
        Assert.Empty(History(emulator));
        Assert.Equal((5, 7), (emulator.CursorRow, emulator.CursorCol));
    }

    [Fact]
    public void TheEmulatorDropsWhatANarrowerScreenCutsOffWhereTheViewerOnlyHidesIt()
    {
        // A known difference, found in review (VB-G9STX-427, part B). xterm.js keeps the cells
        // beyond a narrower width and shows them again when the terminal is widened
        // (xterm-device-queries.spec.js, "text beyond the right edge is hidden, not lost").
        // TerminalBuffer.Resize drops them, so a snapshot taken after a shrink and a grow has
        // less text than the live viewer. If this fails the emulator has learned to keep them:
        // take the limit out of the Terminal guide.
        var emulator = new Emulator(cols: 20, rows: 5);
        emulator.Write("ABCDEFGHIJKLMNOPQRS\r\nsecond");

        emulator.Resize(10, 5);
        emulator.Resize(20, 5);

        Assert.Equal("ABCDEFGHIJ", emulator.GetRowText(0));
        Assert.Equal("second", emulator.GetRowText(1));
        Assert.Equal((1, 6), (emulator.CursorRow, emulator.CursorCol));
    }

    [Theory]
    [InlineData(6)]  // the cursor is on the new last row
    [InlineData(8)]
    [InlineData(10)] // not a shrink
    [InlineData(30)]
    public void NothingMovesWhileTheCursorStillFits(int newRows)
    {
        var emulator = new Emulator(cols: 40, rows: 10);
        emulator.Write("one\r\ntwo\r\nthree\r\nfour\r\nfive\r\nprompt>");
        var before = emulator.GetScreenText();

        TerminalPty.KeepCursorLineOnScreen(emulator, newRows);

        Assert.Equal(before, emulator.GetScreenText());
        Assert.Empty(History(emulator));
        Assert.Equal((5, 7), (emulator.CursorRow, emulator.CursorCol));
    }

    [Fact]
    public void MarginsAProgramSetDoNotStopTheScreenFromScrolling()
    {
        // xterm.js resets the margins on every resize; a line feed inside margins that are not
        // the whole screen would scroll only part of it and push nothing into history.
        var emulator = NineLinesAndAPrompt();
        emulator.Write("\u001b[3;8r\u001b[10;8H");

        TerminalPty.KeepCursorLineOnScreen(emulator, newRows: 6);
        emulator.Resize(40, 6);

        Assert.Equal(["line 5", "line 6", "line 7", "line 8", "line 9", "prompt>"], emulator.GetScreenText());
        Assert.Equal(["line 1", "line 2", "line 3", "line 4"], History(emulator));
        Assert.Equal((5, 7), (emulator.CursorRow, emulator.CursorCol));
        // The margins are the whole screen again: a line feed on the last row scrolls all of it.
        emulator.Write("\n");
        Assert.Equal("line 6", emulator.GetRowText(0));
    }

    [Fact]
    public void TheAlternateScreenIsLeftToTheProgramThatOwnsIt()
    {
        var emulator = NineLinesAndAPrompt();
        emulator.Write("\u001b[?1049h\u001b[2J\u001b[10;1Hstatus bar");
        var before = emulator.GetScreenText();

        TerminalPty.KeepCursorLineOnScreen(emulator, newRows: 6);

        Assert.True(emulator.IsAlternateScreen);
        Assert.Equal(before, emulator.GetScreenText());
        Assert.Empty(History(emulator));
    }

    [Fact]
    public void WhatTheCursorPointsAtSurvivesWithItsColoursAndTheCursorKeepsItsColumn()
    {
        var emulator = new Emulator(cols: 20, rows: 8);
        for (var line = 1; line <= 7; line++) emulator.Write($"row {line}\r\n");
        emulator.Write("\u001b[31mred\u001b[0m text\u001b[8;3H");

        TerminalPty.KeepCursorLineOnScreen(emulator, newRows: 3);
        emulator.Resize(20, 3);

        Assert.Equal(["row 6", "row 7", "red text"], emulator.GetScreenText());
        Assert.Equal((2, 2), (emulator.CursorRow, emulator.CursorCol));
        var cell = emulator.GetSnapshot()[2, 0];
        Assert.True(cell.Fg.IsPalette && cell.Fg.PaletteIndex == 1, "the scrolled cells kept their attributes");
        Assert.Equal(5, History(emulator).Length);
    }

    private static Emulator NineLinesAndAPrompt()
    {
        var emulator = new Emulator(cols: 40, rows: 10);
        for (var line = 1; line <= 9; line++) emulator.Write($"line {line}\r\n");
        emulator.Write("prompt>");
        Assert.Equal((9, 7), (emulator.CursorRow, emulator.CursorCol));
        return emulator;
    }

    private static string[] History(Emulator emulator) =>
        [.. emulator.GetScrollback().Select(row =>
        {
            var text = new StringBuilder(row.Length);
            foreach (var cell in row)
            {
                if (!cell.IsWideContinuation) cell.AppendText(text, replaceControlWithSpace: true);
            }
            return text.ToString().TrimEnd();
        })];
}
