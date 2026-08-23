using System.Runtime.InteropServices;
using System.Text;
using Pty.Net;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;
using TerminalPty = VibeRails.Services.Terminal.Terminal;

namespace Tests.Services.Terminal;

public sealed class CodexWindowsInputRewriterTests
{
    private const string Esc = "\u001b";

    private static readonly byte[] Win32ShiftEnterBytes =
        Encoding.ASCII.GetBytes(CodexWindowsInputRewriter.Win32ShiftEnterDown);

    [Fact]
    public void ShouldRewrite_CodexOnWindows_IsTrue()
    {
        Assert.Equal(OperatingSystem.IsWindows(), CodexWindowsInputRewriter.ShouldRewrite(LLM.Codex));
    }

    [Fact]
    public void ShouldRewrite_OtherClis_IsFalse()
    {
        Assert.False(CodexWindowsInputRewriter.ShouldRewrite(LLM.Claude));
        Assert.False(CodexWindowsInputRewriter.ShouldRewrite(LLM.OpenCode));
        Assert.False(CodexWindowsInputRewriter.ShouldRewrite(LLM.NotSet));
    }

    [Fact]
    public void Rewrite_SingleLf_IsExactWin32ShiftEnter()
    {
        var rewriter = new CodexWindowsInputRewriter();

        Assert.Equal(Win32ShiftEnterBytes, rewriter.Rewrite("\n"u8.ToArray()).ToArray());
    }

    [Fact]
    public void Rewrite_CarriageReturnAndSameCallCrLf_AreUnchanged()
    {
        var rewriter = new CodexWindowsInputRewriter();

        Assert.Equal("\r"u8.ToArray(), rewriter.Rewrite("\r"u8.ToArray()).ToArray());
        Assert.Equal("\r\n"u8.ToArray(), rewriter.Rewrite("\r\n"u8.ToArray()).ToArray());
    }

    [Fact]
    public void Rewrite_CrLfSplitAcrossCalls_IsUnchanged()
    {
        var rewriter = new CodexWindowsInputRewriter();

        var output = RewriteChunks(rewriter, "\r"u8.ToArray(), "\n"u8.ToArray());

        Assert.Equal("\r\n"u8.ToArray(), output);
    }

    [Fact]
    public void Rewrite_NoReplacement_ReturnsOriginalMemory()
    {
        var rewriter = new CodexWindowsInputRewriter();
        var bytes = new byte[] { 0xF0, 0x9F, 0x98, 0x80, 0xFF, 0x00 };

        var output = rewriter.Rewrite(bytes);

        Assert.True(MemoryMarshal.TryGetArray(output, out var segment));
        Assert.Same(bytes, segment.Array);
        Assert.Equal(bytes, output.ToArray());
    }

    [Fact]
    public void Rewrite_TypedThenLf_RewritesOnlyLf()
    {
        var rewriter = new CodexWindowsInputRewriter();

        var output = rewriter.Rewrite("hi\n"u8.ToArray()).ToArray();

        Assert.Equal(Concat("hi"u8.ToArray(), Win32ShiftEnterBytes), output);
    }

    [Fact]
    public void Rewrite_PasteStreamSplitAtEveryBoundary_PreservesPasteLf()
    {
        var input = Encoding.ASCII.GetBytes($"{Esc}[200~line one\nline two{Esc}[201~\n");
        var expected = Concat(
            Encoding.ASCII.GetBytes($"{Esc}[200~line one\nline two{Esc}[201~"),
            Win32ShiftEnterBytes);

        for (var split = 1; split < input.Length; split++)
        {
            var rewriter = new CodexWindowsInputRewriter();
            var output = RewriteChunks(
                rewriter,
                input.AsMemory(0, split),
                input.AsMemory(split));

            Assert.True(
                expected.SequenceEqual(output),
                $"split at byte {split}: expected {Convert.ToHexString(expected)}, got {Convert.ToHexString(output)}");
        }
    }

    [Fact]
    public void Rewrite_PasteMarkersOneBytePerCall_PreservesPasteLfThenRewritesOutsideLf()
    {
        var input = Encoding.ASCII.GetBytes($"{Esc}[200~a\nb{Esc}[201~\n");
        var rewriter = new CodexWindowsInputRewriter();

        var output = RewriteChunks(
            rewriter,
            input.Select(value => new ReadOnlyMemory<byte>([value])).ToArray());

        Assert.Equal(
            Concat(Encoding.ASCII.GetBytes($"{Esc}[200~a\nb{Esc}[201~"), Win32ShiftEnterBytes),
            output);
    }

    [Fact]
    public void Rewrite_LfBeforeInsideAndAfterPaste_RewritesOnlyOutsideLf()
    {
        var rewriter = new CodexWindowsInputRewriter();
        var input = Encoding.ASCII.GetBytes($"\n{Esc}[200~a\nb{Esc}[201~\n");

        var output = rewriter.Rewrite(input).ToArray();

        Assert.Equal(
            Concat(
                Win32ShiftEnterBytes,
                Encoding.ASCII.GetBytes($"{Esc}[200~a\nb{Esc}[201~"),
                Win32ShiftEnterBytes),
            output);
    }

    [Fact]
    public void Rewrite_InvalidPasteLikeCsi_DoesNotEnterPasteMode()
    {
        var rewriter = new CodexWindowsInputRewriter();
        var invalidMarker = Encoding.ASCII.GetBytes($"{Esc}[200x\n");

        var output = rewriter.Rewrite(invalidMarker).ToArray();

        Assert.Equal(
            Concat(Encoding.ASCII.GetBytes($"{Esc}[200x"), Win32ShiftEnterBytes),
            output);
    }

    [Fact]
    public void Rewrite_SplitUtf8AndInvalidRawBytes_PreservesEveryNonLfByte()
    {
        var prefix = new byte[]
        {
            0xC3, 0xA9,                         // é
            0xE4, 0xB8, 0xAD,                   // 中
            0xF0, 0x9F, 0x98, 0x80,             // emoji
            0xFF, 0x00, 0xC3                    // invalid/split raw bytes
        };
        var input = Concat(prefix, [(byte)'\n'], [0x28, 0xFE]);
        var rewriter = new CodexWindowsInputRewriter();

        var output = RewriteChunks(
            rewriter,
            input.Select(value => new ReadOnlyMemory<byte>([value])).ToArray());

        Assert.Equal(Concat(prefix, Win32ShiftEnterBytes, [0x28, 0xFE]), output);
    }

    [Fact]
    public void Rewrite_StateIsIsolatedPerInstance()
    {
        var pasteSession = new CodexWindowsInputRewriter();
        var otherSession = new CodexWindowsInputRewriter();

        _ = pasteSession.Rewrite(Encoding.ASCII.GetBytes($"{Esc}[200~"));

        Assert.Equal("\n"u8.ToArray(), pasteSession.Rewrite("\n"u8.ToArray()).ToArray());
        Assert.Equal(Win32ShiftEnterBytes, otherSession.Rewrite("\n"u8.ToArray()).ToArray());
    }

    [Fact]
    public void BreakInputSequence_InvalidatesPartialMarkerAndPriorCr_ButRetainsActivePaste()
    {
        var partialMarker = new CodexWindowsInputRewriter();
        _ = partialMarker.Rewrite(Encoding.ASCII.GetBytes($"{Esc}[2"));
        partialMarker.BreakInputSequence();
        Assert.Equal(
            Concat("00~"u8.ToArray(), Win32ShiftEnterBytes),
            partialMarker.Rewrite("00~\n"u8.ToArray()).ToArray());

        var priorCr = new CodexWindowsInputRewriter();
        _ = priorCr.Rewrite("\r"u8.ToArray());
        priorCr.BreakInputSequence();
        Assert.Equal(Win32ShiftEnterBytes, priorCr.Rewrite("\n"u8.ToArray()).ToArray());

        var activePaste = new CodexWindowsInputRewriter();
        _ = activePaste.Rewrite(Encoding.ASCII.GetBytes($"{Esc}[200~"));
        activePaste.BreakInputSequence();
        Assert.Equal("\n"u8.ToArray(), activePaste.Rewrite("\n"u8.ToArray()).ToArray());
    }

    [Fact]
    public async Task Terminal_RawWriteBreaksPartialMarkerAndCrAdjacency_ButRetainsActivePaste()
    {
        var writer = new MemoryStream();
        await using var terminal = CreateTerminal(writer, rewrite: true);
        var ct = TestContext.Current.CancellationToken;

        await terminal.WriteInputBytesAsync(Encoding.ASCII.GetBytes($"{Esc}[2"), ct);
        await terminal.WriteBytesAsync("X"u8.ToArray(), ct);
        await terminal.WriteInputBytesAsync("00~\n"u8.ToArray(), ct);
        await terminal.WriteInputBytesAsync("\r"u8.ToArray(), ct);
        await terminal.WriteBytesAsync("Y"u8.ToArray(), ct);
        await terminal.WriteInputBytesAsync("\n"u8.ToArray(), ct);
        await terminal.WriteInputBytesAsync(Encoding.ASCII.GetBytes($"{Esc}[200~"), ct);
        await terminal.WriteBytesAsync(new byte[] { 0x0C }, ct);
        await terminal.WriteInputBytesAsync("\n"u8.ToArray(), ct);

        Assert.Equal(
            Concat(
                Encoding.ASCII.GetBytes($"{Esc}[2X00~"),
                Win32ShiftEnterBytes,
                "\rY"u8.ToArray(),
                Win32ShiftEnterBytes,
                Encoding.ASCII.GetBytes($"{Esc}[200~"),
                [0x0C, (byte)'\n']),
            writer.ToArray());
    }

    [Fact]
    public async Task Terminal_NonRewritePath_ForwardsOriginalRawMemory()
    {
        var writer = new MemoryIdentityStream();
        await using var terminal = CreateTerminal(writer, rewrite: false);
        var input = new byte[] { 0xF0, 0x9F, 0x98, 0x80, 0xFF, 0x0A };

        await terminal.WriteInputBytesAsync(input, TestContext.Current.CancellationToken);

        Assert.Same(input, writer.WrittenArray);
        Assert.Equal(input, writer.WrittenBytes);
    }

    [Fact]
    public async Task Terminal_ConcurrentInput_SerializesRewriteStateWithPtyWrite()
    {
        var writer = new BlockingFirstWriteStream();
        await using var terminal = CreateTerminal(writer, rewrite: true);
        var pasteStart = Encoding.ASCII.GetBytes($"{Esc}[200~");
        var ct = TestContext.Current.CancellationToken;

        var firstWrite = terminal.WriteInputBytesAsync(pasteStart, ct);
        await writer.FirstWriteEntered;

        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWrite = Task.Run(async () =>
        {
            secondStarted.SetResult();
            await terminal.WriteInputBytesAsync("\n"u8.ToArray(), ct);
        }, ct);
        await secondStarted.Task;
        await Task.Delay(25, ct);

        Assert.Equal(1, writer.WriteCallCount);

        writer.ReleaseFirstWrite();
        await Task.WhenAll(firstWrite, secondWrite);

        Assert.Equal(2, writer.WriteCallCount);
        Assert.Equal(Concat(pasteStart, "\n"u8.ToArray()), writer.WrittenBytes);
    }

    [Fact]
    public void Win32ShiftEnterDown_MatchesConPtyWin32InputMode()
    {
        Assert.Equal($"{Esc}[13;28;0;1;16;1_", CodexWindowsInputRewriter.Win32ShiftEnterDown);
        Assert.Equal(
            Encoding.ASCII.GetBytes($"{Esc}[13;28;0;1;16;1_"),
            Win32ShiftEnterBytes);
    }

    private static TerminalPty CreateTerminal(Stream writer, bool rewrite)
    {
        return new TerminalPty(new TestPtyConnection(writer), cols: 80, rows: 24)
        {
            EncodeBareLineFeedAsWin32ShiftEnter = rewrite
        };
    }

    private static byte[] RewriteChunks(
        CodexWindowsInputRewriter rewriter,
        params ReadOnlyMemory<byte>[] chunks)
    {
        var output = new List<byte>();
        foreach (var chunk in chunks)
            output.AddRange(rewriter.Rewrite(chunk).ToArray());
        return output.ToArray();
    }

    private static byte[] Concat(params byte[][] chunks)
    {
        var output = new byte[chunks.Sum(chunk => chunk.Length)];
        var offset = 0;
        foreach (var chunk in chunks)
        {
            chunk.CopyTo(output, offset);
            offset += chunk.Length;
        }
        return output;
    }

    private sealed class TestPtyConnection(Stream writer) : IPtyConnection
    {
        public event EventHandler<PtyExitedEventArgs>? ProcessExited
        {
            add { }
            remove { }
        }
        public Stream ReaderStream { get; } = Stream.Null;
        public Stream WriterStream { get; } = writer;
        public int Pid => 0;
        public int ExitCode => 0;
        public bool WaitForExit(int milliseconds) => true;
        public void Kill() { }
        public void KillProcessTree() { }
        public void Resize(int cols, int rows) { }
        public void Dispose() => WriterStream.Dispose();
    }

    private sealed class MemoryIdentityStream : MemoryStream
    {
        public byte[]? WrittenArray { get; private set; }
        public byte[] WrittenBytes => ToArray();

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (MemoryMarshal.TryGetArray(buffer, out var segment))
                WrittenArray = segment.Array;
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    private sealed class BlockingFirstWriteStream : Stream
    {
        private readonly MemoryStream _written = new();
        private readonly TaskCompletionSource _firstWriteEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseFirstWrite =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeCallCount;

        public Task FirstWriteEntered => _firstWriteEntered.Task;
        public int WriteCallCount => Volatile.Read(ref _writeCallCount);
        public byte[] WrittenBytes
        {
            get
            {
                lock (_written)
                    return _written.ToArray();
            }
        }

        public void ReleaseFirstWrite() => _releaseFirstWrite.TrySetResult();

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var copy = buffer.ToArray();
            var call = Interlocked.Increment(ref _writeCallCount);
            if (call == 1)
            {
                _firstWriteEntered.TrySetResult();
                await _releaseFirstWrite.Task.WaitAsync(cancellationToken);
            }

            lock (_written)
                _written.Write(copy);
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
