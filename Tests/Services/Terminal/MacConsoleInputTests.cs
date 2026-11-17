using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>Exercises the production input reader against a disposable Darwin PTY, never the user's console.</summary>
public sealed class MacConsoleInputTests
{
    public static bool IsMacOS => OperatingSystem.IsMacOS();
    private const string RequiresMac = "Requires a macOS PTY";

    [Fact(SkipWhen = nameof(IsMacOS), Skip = "Only checks the non-macOS platform gate")]
    public void OpenConsole_LeavesOtherPlatformsAlone() => Assert.Null(MacConsoleInput.OpenConsole());

    [Theory(SkipUnless = nameof(IsMacOS), Skip = RequiresMac)]
    [InlineData("\u001b[<35;34;10M\u001b[<35;33;10M\u001b[<35;31;9M")]
    [InlineData("\u001b[<64;55;30M\u001b[<65;55;30M\u001b[<0;4;5m")]
    [InlineData("\u001b[I\u001b[O\u001b[12;40R")]
    [InlineData("\u001b[A\u001b[Z\u001b[13;2u\u001b[27;2;13~")]
    [InlineData("\u001b[200~first\r\nsecond\n雪😀\u001b[201~\r")]
    [InlineData("\u001bx\u007f\t\r\n\u0003\u0004\u0013\u0011")]
    public async Task ReadAvailable_PreservesReportsKeysAndPasteAcrossEverySplit(string input)
    {
        using var pty = new TestPty();
        using var reader = new MacConsoleInput(pty.Slave);
        var expected = Encoding.UTF8.GetBytes(input);
        var buffer = new byte[4096];

        for (var split = 0; split <= expected.Length; split++)
        {
            // Wait for each piece before writing its continuation, so the reader really sees
            // split ESC, CSI and UTF-8 bytes rather than a coalesced kernel read.
            foreach (var part in new[] { expected[..split], expected[split..] })
            {
                if (part.Length == 0)
                    continue;
                pty.Send(part);
                var actual = new List<byte>();
                var clock = Stopwatch.StartNew();
                while (actual.Count < part.Length && clock.Elapsed < TimeSpan.FromSeconds(2))
                {
                    var count = reader.ReadAvailable(buffer);
                    actual.AddRange(buffer.AsSpan(0, count).ToArray());
                    if (count == 0)
                        await Task.Delay(1, TestContext.Current.CancellationToken);
                }
                Assert.Equal(part, actual.ToArray());
            }
        }
        Assert.Equal(0, reader.ReadAvailable(buffer));
    }

    [Fact(SkipUnless = nameof(IsMacOS), Skip = RequiresMac)]
    public void Dispose_RestoresInputAndOutputSettingsAfterFailure()
    {
        using var pty = new TestPty();
        Assert.Equal(0, MacConsoleInput.GetAttributes(pty.Slave, out var before));

        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var reader = new MacConsoleInput(pty.Slave);
            Assert.Equal(0, MacConsoleInput.GetAttributes(pty.Slave, out var during));
            Assert.Equal(before.OutputFlags, during.OutputFlags);
            Assert.Equal((nuint)0, during.LocalFlags & (0x8u | 0x100u | 0x80u)); // ECHO | ICANON | ISIG
            Assert.Equal(0, reader.ReadAvailable(new byte[32])); // no input must not block
            throw new InvalidOperationException("Simulated cancellation/failure in the relay");
        }));

        Assert.Equal(0, MacConsoleInput.GetAttributes(pty.Slave, out var after));
        Assert.Equal(before.InputFlags, after.InputFlags);
        Assert.Equal(before.OutputFlags, after.OutputFlags);
        Assert.Equal(before.ControlFlags, after.ControlFlags);
        Assert.Equal(before.LocalFlags, after.LocalFlags);
        Assert.Equal(before.ControlCharacters, after.ControlCharacters);
        Assert.Equal(before.InputSpeed, after.InputSpeed);
        Assert.Equal(before.OutputSpeed, after.OutputSpeed);
    }

    [Fact(SkipUnless = nameof(IsMacOS), Skip = RequiresMac)]
    public void Dispose_IsIdempotentAndDoesNotCloseBorrowedDescriptor()
    {
        using var pty = new TestPty();
        var reader = new MacConsoleInput(pty.Slave);
        reader.Dispose();
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.ReadAvailable(new byte[32]));
        using var nextReader = new MacConsoleInput(pty.Slave);
        Assert.Equal(0, nextReader.ReadAvailable(new byte[32]));
    }

    private sealed class TestPty : IDisposable
    {
        private readonly int _master;
        internal int Slave { get; }

        internal TestPty()
        {
            Assert.Equal(0, OpenPty(out _master, out var slave, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero));
            Slave = slave;
        }

        internal void Send(byte[] bytes) => Assert.Equal((nint)bytes.Length, Write(_master, bytes, (nuint)bytes.Length));

        public void Dispose()
        {
            Close(Slave);
            Close(_master);
        }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "openpty", SetLastError = true)]
    private static extern int OpenPty(out int master, out int slave, IntPtr name, IntPtr termios, IntPtr size);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "write", SetLastError = true)]
    private static extern nint Write(int descriptor, byte[] buffer, nuint count);

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "close")]
    private static extern int Close(int descriptor);
}
