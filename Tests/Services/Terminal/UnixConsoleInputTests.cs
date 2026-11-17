using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// Exercises the production input reader against a disposable macOS or Linux PTY, never the
/// user's console. The macOS workflow and the release workflow's Ubuntu test job run these.
/// </summary>
public sealed class UnixConsoleInputTests
{
    public static bool IsUnix => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();
    private const string RequiresUnix = "Requires a macOS or Linux PTY";

    [Fact(SkipWhen = nameof(IsUnix), Skip = "Only checks the Windows platform gate")]
    public void OpenConsole_LeavesWindowsAlone() => Assert.Null(UnixConsoleInput.OpenConsole());

    [Fact(SkipUnless = nameof(IsUnix), Skip = RequiresUnix)]
    public void Layout_MatchesThisPlatformsDefaultTermios()
    {
        // A fresh PTY starts cooked on both platforms: ECHO, ICANON and ISIG set, VMIN 1. Reading
        // those through the layout checks its offsets and flag bits without trusting the reader.
        using var pty = new TestPty();
        AssertLayoutMatches(pty.Slave);
    }

    [Theory(SkipUnless = nameof(IsUnix), Skip = RequiresUnix)]
    [InlineData("\u001b[<35;34;10M\u001b[<35;33;10M\u001b[<35;31;9M")]
    [InlineData("\u001b[<64;55;30M\u001b[<65;55;30M\u001b[<0;4;5m")]
    [InlineData("\u001b[I\u001b[O\u001b[12;40R")]
    [InlineData("\u001b[A\u001b[Z\u001b[13;2u\u001b[27;2;13~")]
    [InlineData("\u001b[200~first\r\nsecond\n雪😀\u001b[201~\r")]
    [InlineData("\u001bx\u007f\t\r\n\u0003\u0004\u0013\u0011\u001a\u001c")]
    public async Task ReadAvailable_PreservesReportsKeysAndPasteAcrossEverySplit(string input)
    {
        using var pty = new TestPty();
        // A wrong VMIN offset would leave read blocking; fail on the layout instead of hanging.
        AssertLayoutMatches(pty.Slave);
        using var reader = new UnixConsoleInput(pty.Slave);
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

    [Fact(SkipUnless = nameof(IsUnix), Skip = RequiresUnix)]
    public void Dispose_RestoresInputAndOutputSettingsAfterFailure()
    {
        using var pty = new TestPty();
        var layout = AssertLayoutMatches(pty.Slave);
        var before = Attributes(pty.Slave);

        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var reader = new UnixConsoleInput(pty.Slave);
            var during = Attributes(pty.Slave);
            Assert.Equal(
                before.AsSpan(layout.OutputFlags, layout.FlagSize).ToArray(),
                during.AsSpan(layout.OutputFlags, layout.FlagSize).ToArray());
            Assert.Equal(0ul, layout.ReadLocalFlags(during) & (layout.Echo | layout.Canonical | layout.Signals));
            Assert.Equal(0, during[layout.MinimumCharacters]);
            Assert.Equal(0, during[layout.TimeoutDeciseconds]);
            Assert.Equal(0, reader.ReadAvailable(new byte[32])); // no input must not block
            throw new InvalidOperationException("Simulated cancellation/failure in the relay");
        }));

        var restored = Attributes(pty.Slave);
        if (OperatingSystem.IsMacOS())
        {
            // Darwin sets PENDIN when TCSANOW restores ICANON so queued input can be
            // reprocessed. It is kernel state, not a setting the reader failed to restore.
            // Assert that transition, then compare every other bit and byte unchanged.
            const ulong pendingInput = 0x20000000;
            Assert.Equal(0ul, layout.ReadLocalFlags(before) & pendingInput);
            Assert.Equal(pendingInput, layout.ReadLocalFlags(restored) & pendingInput);
            var restoredFlags = layout.ReadLocalFlags(restored) & ~pendingInput;
            MemoryMarshal.Write(restored.AsSpan(layout.LocalFlags), in restoredFlags);
        }
        Assert.Equal(before, restored);
    }

    [Fact(SkipUnless = nameof(IsUnix), Skip = RequiresUnix)]
    public void Dispose_IsIdempotentAndDoesNotCloseBorrowedDescriptor()
    {
        using var pty = new TestPty();
        AssertLayoutMatches(pty.Slave);
        var reader = new UnixConsoleInput(pty.Slave);
        reader.Dispose();
        reader.Dispose();
        Assert.Throws<ObjectDisposedException>(() => reader.ReadAvailable(new byte[32]));
        using var nextReader = new UnixConsoleInput(pty.Slave);
        Assert.Equal(0, nextReader.ReadAvailable(new byte[32]));
    }

    private static UnixConsoleInput.TermiosLayout AssertLayoutMatches(int descriptor)
    {
        var layout = UnixConsoleInput.TermiosLayout.Current;
        var cooked = Attributes(descriptor);
        var local = layout.ReadLocalFlags(cooked);
        Assert.Equal(layout.Echo | layout.Canonical | layout.Signals,
            local & (layout.Echo | layout.Canonical | layout.Signals));
        Assert.Equal(1, cooked[layout.MinimumCharacters]);
        return layout;
    }

    private static byte[] Attributes(int descriptor)
    {
        var attributes = new byte[UnixConsoleInput.AttributesSize];
        Assert.Equal(0, UnixConsoleInput.GetAttributes(descriptor, attributes));
        return attributes;
    }

    private sealed class TestPty : IDisposable
    {
        private readonly int _master;
        internal int Slave { get; }

        internal TestPty()
        {
            int slave;
            var opened = OperatingSystem.IsMacOS()
                ? Darwin.OpenPty(out _master, out slave, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero)
                : Linux.OpenPty(out _master, out slave, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            Assert.Equal(0, opened);
            Slave = slave;
        }

        internal void Send(byte[] bytes) => Assert.Equal((nint)bytes.Length, OperatingSystem.IsMacOS()
            ? Darwin.Write(_master, bytes, (nuint)bytes.Length)
            : Linux.Write(_master, bytes, (nuint)bytes.Length));

        public void Dispose()
        {
            Close(Slave);
            Close(_master);
        }

        private static void Close(int descriptor)
        {
            if (OperatingSystem.IsMacOS()) Darwin.Close(descriptor);
            else Linux.Close(descriptor);
        }
    }

    private static class Darwin
    {
        private const string Library = "/usr/lib/libSystem.B.dylib";

        [DllImport(Library, EntryPoint = "openpty", SetLastError = true)]
        internal static extern int OpenPty(out int master, out int slave, IntPtr name, IntPtr termios, IntPtr size);

        [DllImport(Library, EntryPoint = "write", SetLastError = true)]
        internal static extern nint Write(int descriptor, byte[] buffer, nuint count);

        [DllImport(Library, EntryPoint = "close")]
        internal static extern int Close(int descriptor);
    }

    private static class Linux
    {
        // openpty moved into libc in glibc 2.34; libutil.so.1 still resolves it on older and newer
        // glibc, which is why Pty.Net binds forkpty the same way.
        [DllImport("libutil.so.1", EntryPoint = "openpty", SetLastError = true)]
        internal static extern int OpenPty(out int master, out int slave, IntPtr name, IntPtr termios, IntPtr size);

        [DllImport("libc.so.6", EntryPoint = "write", SetLastError = true)]
        internal static extern nint Write(int descriptor, byte[] buffer, nuint count);

        [DllImport("libc.so.6", EntryPoint = "close")]
        internal static extern int Close(int descriptor);
    }
}
