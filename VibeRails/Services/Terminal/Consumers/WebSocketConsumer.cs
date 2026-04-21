using System.Diagnostics;
using System.Net.WebSockets;
using System.Threading.Channels;
using Serilog;

namespace VibeRails.Services.Terminal.Consumers;

/// <summary>
/// Sends PTY output to a WebSocket as binary frames. Used by the Web UI terminal path.
/// Uses a single-writer queue so output frames are sent in-order without concurrent SendAsync calls.
/// </summary>
public sealed class WebSocketConsumer : ITerminalConsumer, IDisposable
{
    private const int NormalCoalesceDelayMs = 4;
    private const int SyncOutputPollDelayMs = 4;
    private const int MaxSyncOutputHoldMs = 100;

    private readonly Channel<OutboundFrame> _outbound = Channel.CreateUnbounded<OutboundFrame>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly WebSocket _webSocket;
    private readonly CancellationToken _ct;
    private readonly Task _sendLoop;
    private readonly Func<bool>? _isSyncOutputActive;

    public WebSocketConsumer(WebSocket webSocket, CancellationToken ct, Func<bool>? isSyncOutputActive = null)
    {
        _webSocket = webSocket;
        _ct = ct;
        _isSyncOutputActive = isSyncOutputActive;
        _sendLoop = Task.Run(() => SendLoopAsync());
    }

    public void OnOutput(ReadOnlyMemory<byte> data)
    {
        if (_webSocket.State != WebSocketState.Open || _ct.IsCancellationRequested)
            return;

        var syncOutputActiveAfterFrame = _isSyncOutputActive?.Invoke() == true;
        _outbound.Writer.TryWrite(new OutboundFrame(data.ToArray(), syncOutputActiveAfterFrame, Stopwatch.GetTimestamp()));
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
                frames.Clear();
                if (!_outbound.Reader.TryRead(out var frame))
                    continue;

                int totalLen = 0;
                var syncOutputActive = false;
                long firstFrameEnqueuedTs = frame.EnqueuedTimestamp;
                AddFrame(frame);
                var batchStarted = Stopwatch.GetTimestamp();
                var coalesceRounds = 0;

                while (true)
                {
                    var delayMs = syncOutputActive
                        ? SyncOutputPollDelayMs
                        : NormalCoalesceDelayMs;
                    var waitForDataTask = _outbound.Reader.WaitToReadAsync(_ct).AsTask();
                    var delayTask = Task.Delay(delayMs, _ct);
                    var completed = await Task.WhenAny(waitForDataTask, delayTask);
                    coalesceRounds++;

                    if (completed == waitForDataTask)
                    {
                        if (!await waitForDataTask.ConfigureAwait(false))
                            break;

                        while (_outbound.Reader.TryRead(out var pendingFrame))
                            AddFrame(pendingFrame);

                        if (!syncOutputActive)
                            continue;
                    }

                    if (!syncOutputActive)
                        break;

                    if (GetElapsedMilliseconds(batchStarted) >= MaxSyncOutputHoldMs)
                        break;
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

                var coalesceHoldMs = Stopwatch.GetElapsedTime(firstFrameEnqueuedTs).TotalMilliseconds;
                var sendStartTs = Stopwatch.GetTimestamp();
                await _webSocket.SendAsync(payload, WebSocketMessageType.Binary, true, _ct);
                var sendMs = Stopwatch.GetElapsedTime(sendStartTs).TotalMilliseconds;
                Log.Debug(
                    "[TypingLag] WS send frames={Frames} bytes={Bytes} coalesceRounds={Rounds} holdMs={HoldMs:F3} sendMs={SendMs:F3} syncOut={SyncOut}",
                    frames.Count, totalLen, coalesceRounds, coalesceHoldMs, sendMs, syncOutputActive);

                void AddFrame(OutboundFrame outboundFrame)
                {
                    frames.Add(outboundFrame.Payload);
                    totalLen += outboundFrame.Payload.Length;
                    syncOutputActive = outboundFrame.SyncOutputActiveAfterFrame;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static long GetElapsedMilliseconds(long startedTimestamp)
    {
        return (long)(Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds);
    }

    private readonly record struct OutboundFrame(
        byte[] Payload,
        bool SyncOutputActiveAfterFrame,
        long EnqueuedTimestamp);
}
