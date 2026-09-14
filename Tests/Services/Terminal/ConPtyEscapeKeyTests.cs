using System.IO.Pipes;
using System.Text;
using Moq;
using Pty.Net;
using VibeRails.Services.Terminal;
using Xunit;
using TerminalPty = VibeRails.Services.Terminal.Terminal;

namespace Tests.Services.Terminal;

/// <summary>
/// Real ConPTY regression for 2026-09-14: the first Win32 Shift+Enter record
/// changes ConPTY's parser so later bare ESC waits for a sequence continuation.
/// No VibeRails host, database, Codex process, or network request is involved.
/// </summary>
public sealed class ConPtyEscapeKeyTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Theory(SkipUnless = nameof(IsWindows), Skip = "Requires Windows ConPTY")]
    [InlineData(false, "")]
    [InlineData(true, "")]
    [InlineData(true, "arrow")]
    [InlineData(true, "paste")]
    public async Task SemanticEscape_ArrivesWithoutAnotherKey_AndDoesNotModifyFollowingKey(bool afterModifiedEnter, string between)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var ct = timeout.Token;
        var pipeName = "vibe_conpty_escape_test_" + Guid.NewGuid().ToString("N");
        await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var script = "Add-Type -TypeDefinition @'\n" + ChildSource + "\n'@\n[EscapeKeyProbe]::Run('" + pipeName + "')";
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var pty = await PtyProvider.SpawnAsync(new PtyOptions
        {
            App = executable,
            CommandLine = ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(script))],
            Cwd = Path.GetTempPath(), Cols = 100, Rows = 30
        }, ct);
        await using var terminal = new TerminalPty(pty, cols: 100, rows: 30)
        {
            EncodeBareLineFeedAsWin32ShiftEnter = true
        };
        terminal.StartReadLoop(); // Drain output continuously so the child cannot block on its output pipe.
        await pipe.WaitForConnectionAsync(ct);
        using var reader = new StreamReader(pipe, leaveOpen: true);
        Assert.Equal("READY", await reader.ReadLineAsync(ct));

        if (afterModifiedEnter)
        {
            await terminal.WriteInputBytesAsync("\n"u8.ToArray(), ct);
            Assert.Equal(new KeyRecord(13, 0, 16), await ReadKeyDownAsync(reader, ct));
        }
        if (between == "arrow")
        {
            await terminal.WriteInputBytesAsync("\u001b[A"u8.ToArray(), ct);
            Assert.Equal(new KeyRecord(38, 0, 256), await ReadKeyDownAsync(reader, ct));
        }
        if (between == "paste")
        {
            await terminal.WriteInputBytesAsync("\u001b[200~a\nb\u001b[201~"u8.ToArray(), ct);
            Assert.Equal(new KeyRecord(65, 97, 0), await ReadKeyDownAsync(reader, ct));
            Assert.Equal(new KeyRecord(13, 10, 8), await ReadKeyDownAsync(reader, ct));
            Assert.Equal(new KeyRecord(66, 98, 0), await ReadKeyDownAsync(reader, ct));
        }

        var state = new Mock<ITerminalStateService>();
        await TerminalIoRouter.RouteEscapeKeyAsync(state.Object, terminal, "synthetic-session", TerminalIoSource.LocalWebUi, ct);
        // This must complete before x is written: a pending raw ESC otherwise
        // becomes Alt+x. Also catches accidental duplicate Escape keydowns.
        Assert.Equal(new KeyRecord(27, 27, 0), await ReadKeyDownAsync(reader, ct));
        await terminal.WriteInputBytesAsync("x"u8.ToArray(), ct);
        Assert.Equal(new KeyRecord(88, 120, 0), await ReadKeyDownAsync(reader, ct));
        state.Verify(s => s.RecordInput("synthetic-session", "\u001b", TerminalIoSource.LocalWebUi), Times.Once);
    }

    private static async Task<KeyRecord> ReadKeyDownAsync(StreamReader reader, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            while (await reader.ReadLineAsync(deadline.Token) is { } line)
            {
                var parts = line.Split(',');
                if (parts[0] == "D")
                {
                    var key = new KeyRecord(int.Parse(parts[1]), int.Parse(parts[2]), int.Parse(parts[3]));
                    // ConPTY may synthesize modifier-only records around Ctrl+Enter
                    // and Alt+character. Preserve modifiers on the actual key event.
                    if (key.VirtualKey is not (16 or 17 or 18)) return key;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Assert.Fail("ConPTY did not deliver the key within two seconds without another input byte.");
        }
        throw new InvalidOperationException("The synthetic ConPTY key reader exited unexpectedly.");
    }

    private readonly record struct KeyRecord(int VirtualKey, int Character, int Modifiers);

    // Windows PowerShell provides an installed CLR host for this tiny child.
    // Named-pipe telemetry preserves the exact key records without interpreting
    // them through a terminal renderer or writing any diagnostic file.
    private const string ChildSource = """
        using System;
        using System.IO;
        using System.IO.Pipes;
        using System.Runtime.InteropServices;
        using System.Text;
        public static class EscapeKeyProbe
        {
            [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
            [DllImport("kernel32.dll")] private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
            [DllImport("kernel32.dll")] private static extern bool SetConsoleMode(IntPtr handle, uint mode);
            [DllImport("kernel32.dll")] private static extern bool ReadConsoleInputW(IntPtr handle, out InputRecord record, uint length, out uint count);
            [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode, Size = 20)]
            private struct InputRecord
            {
                [FieldOffset(0)] public ushort EventType;
                [FieldOffset(4)] public int KeyDown;
                [FieldOffset(10)] public ushort VirtualKey;
                [FieldOffset(14)] public char Character;
                [FieldOffset(16)] public uint ControlKeyState;
            }
            public static void Run(string pipeName)
            {
                using (var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
                {
                    pipe.Connect(10000);
                    using (var writer = new StreamWriter(pipe, new UTF8Encoding(false)))
                    {
                        writer.AutoFlush = true;
                        var input = GetStdHandle(-10);
                        uint mode;
                        if (!GetConsoleMode(input, out mode) || !SetConsoleMode(input, (mode & ~(2u | 4u | 512u)) | 8u))
                            throw new InvalidOperationException("Unable to enter legacy console input mode");
                        writer.WriteLine("READY");
                        while (true)
                        {
                            InputRecord record;
                            uint count;
                            if (!ReadConsoleInputW(input, out record, 1, out count))
                                throw new InvalidOperationException("ReadConsoleInputW failed");
                            if (count == 0 || record.EventType != 1) continue;
                            writer.WriteLine((record.KeyDown == 0 ? "U" : "D") + "," + record.VirtualKey + "," + (int)record.Character + "," + record.ControlKeyState);
                        }
                    }
                }
            }
        }
        """;
}
