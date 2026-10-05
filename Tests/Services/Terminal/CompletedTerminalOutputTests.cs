using System.Collections;
using System.Reflection;
using System.Text;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;
using Moq;
using Pty.Net;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Environments.Steps;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.Terminal;
using VibeRails.Services.Terminal.Consumers;
using Xunit;
using TerminalPty = VibeRails.Services.Terminal.Terminal;

namespace Tests.Services.Terminal;

[Collection("ProcessEnvIsolation")]
public sealed class CompletedTerminalOutputTests
{
    [Fact]
    public async Task SynchronousNonOwningPipeCannotHoldDisposalOrDispatchAfterItReturns()
    {
        // Same synchronous/non-owning FileStream semantics as the Unix PTY provider.
        using var writer = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.None);
        using var handle = new SafeFileHandle(writer.ClientSafePipeHandle.DangerousGetHandle(), ownsHandle: false);
        using var reader = new SignalledPipeStream(handle);
        var pty = new Mock<IPtyConnection>();
        pty.SetupGet(p => p.ReaderStream).Returns(reader);
        pty.Setup(p => p.Dispose()).Callback(reader.Dispose);
        await using var terminal = new TerminalPty(pty.Object, 80, 24);
        var live = new List<byte>();
        terminal.Subscribe(new CapturingConsumer(live));
        terminal.StartReadLoop();
        await reader.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var readLoop = (Task)typeof(TerminalPty).GetField("_readLoop", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(terminal)!;
        try
        {
            await terminal.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
            pty.Verify(p => p.Dispose(), Times.Once);
            Assert.False(readLoop.IsCompleted); // Disposing this provider really did not release the read.
            writer.Write("late output"u8);
        }
        finally { writer.Dispose(); }
        await readLoop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Empty(live);
    }

    private sealed class SignalledPipeStream(SafeFileHandle handle) : FileStream(handle, FileAccess.Read, 1024, isAsync: false)
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            return base.ReadAsync(buffer, cancellationToken);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task OwnedSessionFinalizesRecordingBeforeAnnouncingAgentClosure(bool byAgent, bool readReleasedByDisposal)
    {
        var writes = new List<TerminalOutputWrite>();
        var completed = false;
        var disposed = false;
        var repository = new Mock<IRepository>();
        repository.Setup(r => r.PersistTerminalOutputAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<TerminalOutputWrite>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<TerminalOutputWrite>, CancellationToken>((_, rows, _) => { lock (writes) writes.AddRange(rows); })
            .Returns(Task.CompletedTask);
        repository.Setup(r => r.CompleteSessionAsync(It.IsAny<string>(), It.IsAny<int>()))
            .Callback(() => { Assert.True(disposed); completed = true; }).Returns(Task.CompletedTask);
        using var state = new TerminalStateService(repository.Object, Mock.Of<IGitService>(),
            Mock.Of<IRemoteStateService>(), Mock.Of<ITerminalIoObserverService>());
        var sessionId = await state.CreateSessionAsync("shell", "/project", null,
            ct: TestContext.Current.CancellationToken, jobRunId: "test-no-remote");
        var bus = new AppEventBus();
        var events = new List<AppEvent>();
        using var subscription = bus.Subscribe(e =>
        {
            Assert.True(completed);
            Assert.Contains(writes, w => System.Text.Encoding.UTF8.GetString(w.Data).Contains(AgentSessionClosedPayload.Reason));
            events.Add(e);
        });
        var runner = new TerminalRunner(state, Mock.Of<ICommandService>(), Mock.Of<ILocalToolApiContext>(),
            Mock.Of<ILlmProxySessionState>(), Mock.Of<IAutomationConsumer>(), repository.Object,
            Mock.Of<IEnvironmentStepRunner>(), bus);
        var service = new TerminalSessionService(state, runner, Mock.Of<ILocalClientTracker>());
        // This stream ignores cancellation and releases its final bytes only when the PTY
        // closes, reproducing the last-dispatch/disposal race without a real CLI or database.
        using var output = new DisposalOutputStream();
        var pty = new Mock<IPtyConnection>();
        pty.SetupGet(p => p.ReaderStream).Returns(output);
        pty.Setup(p => p.Dispose()).Callback(() => { disposed = true; if (readReleasedByDisposal) output.Release(); });
        await using var terminal = new TerminalPty(pty.Object, 100, 10);
        terminal.Subscribe(new DbLoggingConsumer(state, sessionId));
        var live = new List<byte>();
        terminal.Subscribe(new CapturingConsumer(live));
        service.RegisterExternalTerminal(terminal, sessionId, "/project");
        typeof(TerminalSessionService).GetField("s_externallyOwned", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
        terminal.StartReadLoop();
        await output.Started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        try
        {
            if (byAgent)
            {
                Assert.True(await service.CompleteAgentSessionAsync(sessionId));
                Assert.True(await service.CompleteAgentSessionAsync(sessionId));
                pty.Verify(p => p.KillProcessTree(), Times.Once);
            }
            // A provider that ignores cancellation AND disposal must not hold the lifecycle gate.
            await service.StopSessionAsync().WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
            await service.StopSessionAsync();
            Assert.False(await service.CompleteAgentSessionAsync(sessionId));
            output.Release();
            var readLoop = (Task)typeof(TerminalPty).GetField("_readLoop", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(terminal)!;
            await readLoop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(readReleasedByDisposal ? "last output after pipe closure" : "", Encoding.UTF8.GetString(live.ToArray()));
            var raw = writes.Where(w => w.Kind == TerminalOutputKind.Legacy).ToArray();
            if (readReleasedByDisposal) Assert.Equal("last output after pipe closure", Encoding.UTF8.GetString(raw[0].Data));
            Assert.Equal((byAgent ? 1 : 0) + (readReleasedByDisposal ? 1 : 0), raw.Length);
            if (byAgent) Assert.Contains(AgentSessionClosedPayload.Reason, Encoding.UTF8.GetString(raw[^1].Data));
            Assert.Equal(byAgent ? 1 : 0, events.Count);
            if (byAgent) Assert.Equal("agent_session_closed", events[0].Type);
            repository.Verify(r => r.CompleteSessionAsync(sessionId, 0), Times.Once);
            Assert.NotNull(await service.CaptureSnapshotAsync(TestContext.Current.CancellationToken)); // Recording/snapshot survives the live session.
        }
        finally { output.Release(); await service.UnregisterTerminalAsync(); }
    }

    [Fact]
    public async Task AgentClosureWaitsForAPostExitRunTheOtherEndOwnerStarted()
    {
        // VB-PRTTC-153 R1. Exit cleanup calls RunPostStepsAsync once its own shutdown returns; here
        // it claims the post-exit context first, the losing order the review reproduced, so the
        // stop finds nothing to run itself and must wait for that run before announcing closure.
        var ct = TestContext.Current.CancellationToken;
        var id = "post-exit-owner-" + Guid.NewGuid().ToString("N");
        var tornDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new Mock<ITerminalStateService>();
        state.Setup(s => s.CompleteSessionAsync(id, It.IsAny<int>())).Returns(() => { tornDown.TrySetResult(); return Task.CompletedTask; });
        var stepEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stepRelease = new TaskCompletionSource<StepRunSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = new Mock<IEnvironmentStepRunner>();
        steps.Setup(s => s.RunPhaseAsync(17, EnvironmentStepPhase.PostExit, "/project", null, It.IsAny<CancellationToken>()))
            .Returns(() => { stepEntered.TrySetResult(); return stepRelease.Task; });
        var bus = new AppEventBus();
        var closed = 0;
        using var subscription = bus.Subscribe(e => { if (e.Type == "agent_session_closed") Interlocked.Increment(ref closed); });
        var runner = new TerminalRunner(state.Object, Mock.Of<ICommandService>(), Mock.Of<ILocalToolApiContext>(),
            Mock.Of<ILlmProxySessionState>(), Mock.Of<IAutomationConsumer>(), Mock.Of<IRepository>(), steps.Object, bus);
        var service = new TerminalSessionService(state.Object, runner, Mock.Of<ILocalClientTracker>());
        await using var terminal = new TerminalPty(Mock.Of<IPtyConnection>(), 80, 24);
        // The launch path registers this context only for an environment with post-exit steps.
        var contexts = (IDictionary)typeof(TerminalRunner).GetField("s_postStepContexts", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        contexts[id] = Activator.CreateInstance(typeof(TerminalRunner).GetNestedType("PostStepContext", BindingFlags.NonPublic)!, 17, "env", "/project")!;
        service.RegisterExternalTerminal(terminal, id, "/project");
        typeof(TerminalSessionService).GetField("s_externallyOwned", BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, false);
        try
        {
            terminal.CompleteByAgent();
            var exitCleanup = runner.RunPostStepsAsync(id, 0, ct);
            await stepEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            var stop = service.StopSessionAsync();
            await tornDown.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await Task.Delay(100, ct);
            Assert.False(stop.IsCompleted, "the stop must wait for the post-exit run already in flight");
            Assert.Equal(0, Volatile.Read(ref closed));

            stepRelease.SetResult(StepRunSummary.Empty);
            await Task.WhenAll(stop, exitCleanup).WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(1, Volatile.Read(ref closed));
            steps.Verify(s => s.RunPhaseAsync(17, EnvironmentStepPhase.PostExit, "/project", null, It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { stepRelease.TrySetResult(StepRunSummary.Empty); await service.UnregisterTerminalAsync(); }
    }

    private sealed class CapturingConsumer(List<byte> bytes) : ITerminalConsumer
    {
        public void OnOutput(ReadOnlyMemory<byte> data) => bytes.AddRange(data.ToArray());
    }

    private sealed class DisposalOutputStream : MemoryStream
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => release.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await release.Task;
            var bytes = "last output after pipe closure"u8.ToArray();
            bytes.CopyTo(buffer);
            return bytes.Length;
        }
    }

    [Fact]
    public async Task AgentCompletionStopsOnlyTheExpectedPtyIncludingExternallyOwnedWorkers()
    {
        var pty = new Mock<IPtyConnection>();
        pty.SetupGet(p => p.ExitCode).Returns(137);
        await using var terminal = new TerminalPty(pty.Object, 40, 5);
        var service = new TerminalSessionService(Mock.Of<ITerminalStateService>(), null!, Mock.Of<ILocalClientTracker>());
        service.RegisterExternalTerminal(terminal, "new-worker", "/project");
        try
        {
            Assert.False(await service.CompleteAgentSessionAsync("old-worker"));
            pty.Verify(p => p.KillProcessTree(), Times.Never);
            Assert.True(await service.CompleteAgentSessionAsync("new-worker"));
            pty.Verify(p => p.KillProcessTree(), Times.Once);
            Assert.Equal(0, terminal.ExitCode);
        }
        finally { await service.UnregisterTerminalAsync(); }
    }

    [Fact]
    public async Task FinalOutputSurvivesPtyDisposalAndStopsBeingAvailableWhenTheHostIsReused()
    {
        var pty = new Mock<IPtyConnection>();
        await using var terminal = new TerminalPty(pty.Object, 40, 5);
        var emulator = Emulator(terminal);
        emulator.Write(string.Join("\r\n", Enumerable.Range(0, 30).Select(i => $"review line {i}")));
        var service = new TerminalSessionService(Mock.Of<ITerminalStateService>(), null!, Mock.Of<ILocalClientTracker>());
        service.RegisterExternalTerminal(terminal, "completed", "/project");
        // Model the owned browser PTY without launching a shell or touching application state.
        typeof(TerminalSessionService).GetField("s_externallyOwned", BindingFlags.NonPublic | BindingFlags.Static)!
            .SetValue(null, false);
        try
        {
            await service.StopSessionAsync();
            Assert.False(service.HasActiveSession);
            Assert.Null(service.ActiveSessionId);
            pty.Verify(value => value.Dispose(), Times.Once);
            var snapshot = await service.CaptureSnapshotAsync(TestContext.Current.CancellationToken);
            Assert.NotNull(snapshot);
            Assert.Equal("completed", snapshot.SessionId);
            Assert.True(snapshot.XtermUiBytes!.IncludesScrollback);
            var viewer = new TerminalEmulator.Terminal(cols: 40, rows: 5, scrollbackSize: 100);
            viewer.Write(Convert.FromBase64String(snapshot.XtermUiBytes.Base64).AsSpan());
            var history = viewer.GetScrollback().Select(row =>
            {
                var text = new StringBuilder();
                foreach (var cell in row) cell.AppendText(text);
                return text.ToString().TrimEnd();
            });
            Assert.Equal(Enumerable.Range(0, 30).Select(i => $"review line {i}"),
                history.Concat(viewer.GetScreenText()));
            Assert.False((await service.SendInputAsync(new("ignored"), TestContext.Current.CancellationToken)).Success);

            await using var next = new TerminalPty(Mock.Of<IPtyConnection>(), 40, 5);
            service.RegisterExternalTerminal(next, "next", "/project");
            Assert.Equal("next", (await service.CaptureSnapshotAsync(TestContext.Current.CancellationToken))!.SessionId);
            await service.UnregisterTerminalAsync();
            Assert.Null(await service.CaptureSnapshotAsync(TestContext.Current.CancellationToken));
        }
        finally { await service.UnregisterTerminalAsync(); }
    }

    [Fact]
    public async Task CompletedAlternateScreenBecomesScrollableWithoutChangingTheLiveEmulator()
    {
        await using var terminal = new TerminalPty(Mock.Of<IPtyConnection>(), 40, 5);
        var emulator = Emulator(terminal);
        emulator.Write("\x1b[?1049h\x1b[?1000;1006;2004hReview complete");
        var completed = terminal.CaptureCompletedSnapshotData();
        var viewer = new TerminalEmulator.Terminal(cols: 40, rows: 5, scrollbackSize: 100);
        viewer.Write(completed.XtermReplayBytes.AsSpan());
        Assert.False(viewer.IsAlternateScreen);
        Assert.False(viewer.BracketedPasteActive);
        Assert.Empty(viewer.GetInputReportingModes());
        Assert.False(viewer.CursorVisible);
        Assert.Contains("Review complete", viewer.GetScreenText()[0]);
        Assert.True(emulator.IsAlternateScreen);
        Assert.True(emulator.BracketedPasteActive);
        var live = new TerminalEmulator.Terminal(cols: 40, rows: 5, scrollbackSize: 100);
        live.Write(terminal.CaptureSnapshotData().XtermReplayBytes.AsSpan());
        Assert.True(live.IsAlternateScreen);
    }

    private static TerminalEmulator.Terminal Emulator(TerminalPty terminal) =>
        (TerminalEmulator.Terminal)typeof(TerminalPty).GetField("_emulator", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(terminal)!;
}
