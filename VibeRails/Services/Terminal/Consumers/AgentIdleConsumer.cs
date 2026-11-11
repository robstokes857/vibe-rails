using Serilog;
using VibeRails.Launches;

namespace VibeRails.Services.Terminal.Consumers;

/// <summary>Closes one Agent's PTY after two minutes without output; never owns its host or another session.</summary>
internal sealed class AgentIdleConsumer : ITerminalConsumer
{
    internal static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);
    private readonly object gate = new();
    private readonly Action complete;
    private readonly TimeProvider clock;
    private readonly ITimer timer;
    private long lastOutput;
    private bool closed;
    private bool closing;

    internal AgentIdleConsumer(Action complete, TimeProvider? clock = null)
    {
        this.complete = complete;
        this.clock = clock ?? TimeProvider.System;
        lastOutput = this.clock.GetTimestamp();
        timer = this.clock.CreateTimer(_ => CheckIdle(), null, ScanInterval, ScanInterval);
    }

    internal static void Attach(Terminal terminal, LaunchPreset? preset, TimeProvider? clock = null)
    {
        if (preset?.Kind == "agent")
            terminal.Subscribe(new AgentIdleConsumer(terminal.CompleteAfterIdle, clock));
    }

    public void OnOutput(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty) return;
        lock (gate)
        {
            if (!closed && !closing) lastOutput = clock.GetTimestamp();
        }
    }

    public void OnClosed()
    {
        lock (gate) closed = true;
        timer.Dispose();
    }

    private void CheckIdle()
    {
        lock (gate)
        {
            if (closed || closing || clock.GetElapsedTime(lastOutput) < IdleTimeout) return;
            closing = true;
        }

        // Commit expiry against output under the lock, but never kill a PTY from its output callback.
        // The closure captures this terminal object, so a replacement session cannot inherit it.
        try
        {
            complete();
            timer.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[Terminal] Could not close idle Agent PTY; retrying on the next scan");
            lock (gate) closing = false;
        }
    }
}
