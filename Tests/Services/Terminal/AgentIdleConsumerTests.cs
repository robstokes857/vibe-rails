using Moq;
using Pty.Net;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using VibeRails.Services.Terminal.Consumers;
using Xunit;
using TerminalPty = VibeRails.Services.Terminal.Terminal;

namespace Tests.Services.Terminal;

public sealed class AgentIdleConsumerTests
{
    [Fact]
    public void TwoMinutesWithoutOutputCompletesOnlyOnce()
    {
        var clock = new IdleClock();
        var completions = 0;
        var consumer = new AgentIdleConsumer(() => completions++, clock);
        try
        {
            clock.Advance(119);
            Assert.Equal(0, completions);
            clock.Advance(1);
            Assert.Equal(1, completions);
            clock.Advance(120);
            Assert.Equal(1, completions);
        }
        finally { consumer.OnClosed(); }
    }

    [Fact]
    public void EveryNonemptyChunkRestartsTheIdleWindow()
    {
        var clock = new IdleClock();
        var completions = 0;
        var consumer = new AgentIdleConsumer(() => completions++, clock);
        try
        {
            clock.Advance(119);
            consumer.OnOutput(new byte[] { 0 }); // Even invisible PTY output is activity.
            clock.Advance(119);
            Assert.Equal(0, completions);
            consumer.OnOutput(ReadOnlyMemory<byte>.Empty);
            clock.Advance(1);
            Assert.Equal(1, completions);
        }
        finally { consumer.OnClosed(); }
    }

    [Fact]
    public void ClosedSessionCannotExpireLater()
    {
        var clock = new IdleClock();
        var completions = 0;
        var consumer = new AgentIdleConsumer(() => completions++, clock);
        consumer.OnClosed();
        consumer.OnClosed();
        consumer.OnOutput(new byte[] { 1 });
        clock.Advance(120);
        Assert.Equal(0, completions);
    }

    [Fact]
    public void FailedTeardownRetriesWithoutThrowingFromTimer()
    {
        var clock = new IdleClock();
        var attempts = 0;
        var consumer = new AgentIdleConsumer(() =>
        {
            if (++attempts == 1) throw new InvalidOperationException("Temporary teardown failure");
        }, clock);
        try
        {
            clock.Advance(120);
            clock.Advance(1);
            clock.Advance(1);
            Assert.Equal(2, attempts);
        }
        finally { consumer.OnClosed(); }
    }

    [Theory]
    [InlineData("Do the work", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public async Task OnlyPromptBearingPresetsOwnAnIdleDeadline(string? prompt, bool closes)
    {
        var clock = new IdleClock();
        var pty = new Mock<IPtyConnection>();
        pty.SetupGet(p => p.ExitCode).Returns(137);
        await using var terminal = new TerminalPty(pty.Object, 80, 24);
        var preset = new LaunchPreset("preset", "Agent", "", LLM.Codex, new(), [], prompt,
            true, 1, DateTime.UtcNow, DateTime.UtcNow);
        AgentIdleConsumer.Attach(terminal, preset, clock);
        clock.Advance(120);
        pty.Verify(p => p.KillProcessTree(), closes ? Times.Once : Times.Never);
        Assert.Equal(closes ? AgentSessionClosedPayload.IdleReason : null, terminal.AgentCompletionReason);
        Assert.Equal(closes ? 0 : 137, terminal.ExitCode);
        Assert.False(terminal.CompletedByAgent); // Inactivity must not claim an explicit MCP call.
    }

    [Fact]
    public async Task ExpiryIsScopedToItsPtyAndDoesNotStopActiveOrOrdinaryTerminals()
    {
        var clock = new IdleClock();
        var idlePty = new Mock<IPtyConnection>();
        var busyPty = new Mock<IPtyConnection>();
        var ordinaryPty = new Mock<IPtyConnection>();
        await using var idle = new TerminalPty(idlePty.Object, 80, 24);
        await using var busy = new TerminalPty(busyPty.Object, 80, 24);
        await using var ordinary = new TerminalPty(ordinaryPty.Object, 80, 24);
        var preset = new LaunchPreset("preset", "Agent", "", LLM.Codex, new(), [], "Work",
            true, 1, DateTime.UtcNow, DateTime.UtcNow);
        AgentIdleConsumer.Attach(idle, preset, clock);
        AgentIdleConsumer.Attach(busy, preset, clock);
        AgentIdleConsumer.Attach(ordinary, null, clock);
        clock.Advance(90);
        busy.PublishOutput("Still working"u8.ToArray());
        clock.Advance(30);
        idlePty.Verify(p => p.KillProcessTree(), Times.Once);
        busyPty.Verify(p => p.KillProcessTree(), Times.Never);
        ordinaryPty.Verify(p => p.KillProcessTree(), Times.Never);

        // Disposing the old PTY cancels its monitor; a reused tab's new PTY has no old deadline.
        await busy.DisposeAsync();
        clock.Advance(120);
        busyPty.Verify(p => p.KillProcessTree(), Times.Never);
        ordinaryPty.Verify(p => p.KillProcessTree(), Times.Never);
    }

    [Fact]
    public async Task IdleExpiryTerminatesARealWindowsPtyAndItsDescendant()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Exercises Windows ConPTY process-tree cleanup.");
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "PowerShell", "7", "pwsh.exe");
        Assert.SkipUnless(File.Exists(executable), "PowerShell 7 is required for this PTY fixture.");
        var ct = TestContext.Current.CancellationToken;
        const string command = """
            $child = Start-Process -FilePath (Join-Path $PSHOME 'pwsh.exe') -ArgumentList '-NoProfile', '-NonInteractive', '-Command', 'Start-Sleep -Seconds 60' -WindowStyle Hidden -PassThru
            [Console]::WriteLine('AGENT_IDLE_CHILD:' + $child.Id)
            Start-Sleep -Seconds 60
            """;
        await using var terminal = await TerminalPty.CreateAsync(Path.GetTempPath(), new Dictionary<string, string>(),
            app: executable, argv: ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", command], ct: ct);
        var clock = new IdleClock();
        var preset = new LaunchPreset("real-pty", "Agent", "", LLM.Codex, new(), [], "Work",
            true, 1, DateTime.UtcNow, DateTime.UtcNow);
        var output = new ChildPidConsumer();
        terminal.Subscribe(output);
        AgentIdleConsumer.Attach(terminal, preset, clock);
        var exited = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        terminal.Exited += (_, code) => exited.TrySetResult(code);
        terminal.StartReadLoop();
        var childPid = await output.Pid.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        using var parent = Process.GetProcessById(terminal.Pid);
        using var child = Process.GetProcessById(childPid);
        Assert.False(parent.HasExited);
        Assert.False(child.HasExited);

        clock.Advance(120);

        Assert.Equal(0, await exited.Task.WaitAsync(TimeSpan.FromSeconds(10), ct));
        await Task.WhenAll(parent.WaitForExitAsync(ct), child.WaitForExitAsync(ct))
            .WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.True(parent.HasExited);
        Assert.True(child.HasExited);
        Assert.Equal(AgentSessionClosedPayload.IdleReason, terminal.AgentCompletionReason);
    }

    private sealed class ChildPidConsumer : ITerminalConsumer
    {
        private readonly StringBuilder output = new();
        public TaskCompletionSource<int> Pid { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void OnOutput(ReadOnlyMemory<byte> data)
        {
            output.Append(Encoding.UTF8.GetString(data.Span));
            var match = Regex.Match(output.ToString(), @"AGENT_IDLE_CHILD:(\d+)");
            if (match.Success) Pid.TrySetResult(int.Parse(match.Groups[1].Value));
        }
    }

    private sealed class IdleClock : TimeProvider
    {
        private long timestamp;
        private readonly List<ClockTimer> timers = [];
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ClockTimer(() => callback(state));
            timers.Add(timer);
            return timer;
        }
        public void Advance(int seconds)
        {
            timestamp += TimeSpan.FromSeconds(seconds).Ticks;
            foreach (var timer in timers.ToArray()) timer.Fire();
        }
        private sealed class ClockTimer(Action callback) : ITimer
        {
            private bool disposed;
            public void Fire() { if (!disposed) callback(); }
            public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
            public void Dispose() => disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
