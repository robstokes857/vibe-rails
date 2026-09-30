using System.Reflection;
using System.Text.Json;
using Moq;
using Pty.Net;
using TerminalPty = VibeRails.Services.Terminal.Terminal;

// Synthetic output only: exercise production capture/serialization without a process or database.
var fixtures = new List<object>();
foreach (var (cols, rows, lines) in new[] { (40, 1, 4), (40, 5, 5), (40, 5, 6), (40, 5, 30), (80, 24, 100), (40, 24, 20024) })
{
    await using var terminal = new TerminalPty(Mock.Of<IPtyConnection>(), cols, rows);
    var emulator = (TerminalEmulator.Terminal)typeof(TerminalPty)
        .GetField("_emulator", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
    var expected = Enumerable.Range(1, lines).Select(i => $"Review finding {i}").ToArray();
    emulator.Write(string.Join("\r\n", expected));
    emulator.Write("\x1b[1;3H\x1b[?1003;1006;1004;2004h");
    foreach (var completed in new[] { false, true })
    {
        var bytes = completed ? terminal.CaptureCompletedSnapshotData().XtermReplayBytes : terminal.GetGridReplay();
        fixtures.Add(new { cols, rows, completed, alternate = false, expected,
            base64 = Convert.ToBase64String(bytes), cursorRow = emulator.CursorRow, cursorCol = emulator.CursorCol });
    }

    emulator.Write("\x1b[?1049hAlternate review screen");
    fixtures.Add(new { cols, rows, completed = false, alternate = true,
        expected = new[] { "Alternate review screen" }.Concat(Enumerable.Repeat("", rows - 1)).ToArray(),
        base64 = Convert.ToBase64String(terminal.GetGridReplay()),
        cursorRow = emulator.CursorRow, cursorCol = emulator.CursorCol });
}
File.WriteAllText(args.Single(), JsonSerializer.Serialize(fixtures));
