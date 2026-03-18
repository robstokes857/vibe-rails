using System.Net.WebSockets;
using System.Threading.Channels;

namespace VibeRails.Services.Terminal.Consumers;

/// <summary>
/// Sends PTY output to a WebSocket as binary frames. Used by the Web UI terminal path.
/// Uses a single-writer queue so output frames are sent in-order without concurrent SendAsync calls.
/// </summary>
public sealed class WebSocketConsumer : ITerminalConsumer, IDisposable
{
    private readonly Channel<byte[]> _outbound = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly WebSocket _webSocket;
    private readonly CancellationToken _ct;
    private readonly Task _sendLoop;

    public WebSocketConsumer(WebSocket webSocket, CancellationToken ct)
    {
        _webSocket = webSocket;
        _ct = ct;
        _sendLoop = Task.Run(() => SendLoopAsync());
    }

    public void OnOutput(ReadOnlyMemory<byte> data)
    {
        if (_webSocket.State != WebSocketState.Open || _ct.IsCancellationRequested)
            return;

        _outbound.Writer.TryWrite(data.ToArray());
    }

    public void Dispose()
    {
        _outbound.Writer.TryComplete();
    }

    private async Task SendLoopAsync()
    {
        try
        {
            var frames = new List<byte[]>(16);

            while (await _outbound.Reader.WaitToReadAsync(_ct))
            {
                // Brief coalescing window: TUI apps (Codex, Copilot) split their screen
                // updates across multiple small PTY writes that arrive 1-10ms apart —
                // e.g. an erase sequence, then ?2026h (sync-on), then the redrawn content,
                // then ?2026l (sync-off/render). If each arrives as a separate WebSocket
                // frame, xterm.js may render intermediate torn states (blank cells) between
                // the erase and the sync-on. Waiting 4ms gives the PTY reader time to
                // enqueue related writes so they coalesce into one frame and xterm.js
                // processes the whole sequence atomically in a single write() call.
                await Task.Delay(4, _ct);

                frames.Clear();
                int totalLen = 0;
                while (_outbound.Reader.TryRead(out var frame))
                {
                    frames.Add(frame);
                    totalLen += frame.Length;
                }

                if (frames.Count == 0) continue;
                if (_webSocket.State != WebSocketState.Open) return;

                byte[] payload;
                if (frames.Count == 1)
                {
                    payload = frames[0];
                }
                else
                {
                    payload = new byte[totalLen];
                    int offset = 0;
                    foreach (var f in frames)
                    {
                        Buffer.BlockCopy(f, 0, payload, offset, f.Length);
                        offset += f.Length;
                    }
                }

                await _webSocket.SendAsync(payload, WebSocketMessageType.Binary, true, _ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }
}
