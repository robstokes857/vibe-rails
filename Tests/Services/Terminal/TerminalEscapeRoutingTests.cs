using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using Moq;
using Pty.Net;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;
using TerminalPty = VibeRails.Services.Terminal.Terminal;

namespace Tests.Services.Terminal;

public sealed class TerminalEscapeRoutingTests
{
    [Fact]
    public async Task WebSocket_FragmentedEscapeCommandAfterModifiedEnter_WritesKeyAndRecordsLogicalInput()
    {
        using var writer = new MemoryStream();
        await using var terminal = CreateTerminal(writer);
        var state = new Mock<ITerminalStateService>();
        using var socket = new InputWebSocket(
            Text("\n"), Text("__cmd__:esc", end: false), Text("ape"), Text("x"));

        await RunInputLoop(terminal, socket, state.Object);

        Assert.Equal(
            Encoding.ASCII.GetBytes(CodexWindowsInputRewriter.Win32ShiftEnterDown
                + CodexWindowsInputRewriter.Win32EscapeKey + "x"), writer.ToArray());
        state.Verify(s => s.RecordInput("session", "\u001b", TerminalIoSource.LocalWebUi), Times.Once);
        state.Verify(s => s.RecordInput("session", "\n", TerminalIoSource.LocalWebUi), Times.Once);
        state.Verify(s => s.RecordInput("session", "x", TerminalIoSource.LocalWebUi), Times.Once);
        state.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WebSocket_MalformedCommandIsDropped_AndRawSplitCsiRemainsRaw()
    {
        using var writer = new MemoryStream();
        await using var terminal = CreateTerminal(writer);
        var state = new Mock<ITerminalStateService>();
        using var socket = new InputWebSocket(
            Text("\n"), Text("__cmd__:escape:"), Text("__cmd__:escape:extra"),
            Text("\u001b"), Text("["), Text("A"));

        await RunInputLoop(terminal, socket, state.Object);

        Assert.Equal(Encoding.ASCII.GetBytes(
            CodexWindowsInputRewriter.Win32ShiftEnterDown + "\u001b[A"), writer.ToArray());
        state.Verify(s => s.RecordInput("session", "\n", TerminalIoSource.LocalWebUi), Times.Once);
        state.Verify(s => s.RecordInput("session", "\u001b", TerminalIoSource.LocalWebUi), Times.Once);
        state.Verify(s => s.RecordInput("session", "[", TerminalIoSource.LocalWebUi), Times.Once);
        state.Verify(s => s.RecordInput("session", "A", TerminalIoSource.LocalWebUi), Times.Once);
        state.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task WebSocket_EscapeBeforeModifiedEnter_DoesNotActivateWin32Input()
    {
        using var writer = new MemoryStream();
        await using var terminal = CreateTerminal(writer);
        var state = new Mock<ITerminalStateService>();
        using var socket = new InputWebSocket(Text("__cmd__:escape"), Text("x"));

        await RunInputLoop(terminal, socket, state.Object);

        Assert.Equal(new byte[] { 0x1B, (byte)'x' }, writer.ToArray());
    }

    private static TerminalPty CreateTerminal(Stream writer) =>
        new(new InputPty(writer), cols: 80, rows: 24) { EncodeBareLineFeedAsWin32ShiftEnter = true };

    private static InputFrame Text(string text, bool end = true) =>
        new(Encoding.UTF8.GetBytes(text), end);

    private static async Task RunInputLoop(TerminalPty terminal, WebSocket socket, ITerminalStateService state)
    {
        // Exercise the production receive/reassembly/control-dispatch loop without
        // starting a backend, listener, database, installed CLI or real session.
        var method = typeof(TerminalSessionService).GetMethod("WebSocketInputLoopAsync",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var consumer = Mock.Of<ITerminalConsumer>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await (Task)method.Invoke(null, [terminal, socket, consumer, state, "session", timeout.Token])!;
    }

    private sealed record InputFrame(byte[] Bytes, bool End);

    private sealed class InputWebSocket(params InputFrame[] frames) : WebSocket
    {
        private readonly Queue<InputFrame> _frames = new(frames);
        private WebSocketState _state = WebSocketState.Open;
        public override WebSocketCloseStatus? CloseStatus => null;
        public override string? CloseStatusDescription => null;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_frames.TryDequeue(out var frame))
            {
                _state = WebSocketState.CloseReceived;
                return Task.FromResult(new WebSocketReceiveResult(0, WebSocketMessageType.Close, true));
            }
            frame.Bytes.CopyTo(buffer.AsSpan());
            return Task.FromResult(new WebSocketReceiveResult(frame.Bytes.Length, WebSocketMessageType.Text, frame.End));
        }

        public override Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType type, bool end, CancellationToken ct) =>
            Task.CompletedTask;
        public override Task CloseAsync(WebSocketCloseStatus status, string? description, CancellationToken ct) =>
            CloseOutputAsync(status, description, ct);
        public override Task CloseOutputAsync(WebSocketCloseStatus status, string? description, CancellationToken ct)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }
        public override void Abort() => _state = WebSocketState.Aborted;
        public override void Dispose() => _state = WebSocketState.Closed;
    }

    private sealed class InputPty(Stream writer) : IPtyConnection
    {
        public event EventHandler<PtyExitedEventArgs>? ProcessExited { add { } remove { } }
        public Stream ReaderStream => Stream.Null;
        public Stream WriterStream => writer;
        public int Pid => 0;
        public int ExitCode => 0;
        public bool WaitForExit(int milliseconds) => true;
        public void Kill() { }
        public void KillProcessTree() { }
        public void Resize(int cols, int rows) { }
        public void Dispose() { }
    }
}
