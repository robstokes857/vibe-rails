using System.Collections;
using System.Reflection;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Environments.Steps;
using VibeRails.Services.LlmProxy;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// A session's end has two owners (exit cleanup, and an explicit stop or agent completion) and both
/// call <see cref="TerminalRunner.RunPostStepsAsync"/>. The review of VB-PRTTC-153 reproduced the
/// loser claiming the post-exit context first, which turned the winner's wait into a no-op and let
/// it announce the session closed while the steps were still running.
/// </summary>
public class PostExitStepsConcurrencyTests
{
    [Fact]
    public async Task EveryCallerWaitsForTheRunInFlight_AndStepsRunOnce()
    {
        // Register a post-exit context the way the launch path does, under a key only this test uses.
        var flags = BindingFlags.NonPublic | BindingFlags.Static;
        var contexts = (IDictionary)typeof(TerminalRunner).GetField("s_postStepContexts", flags)!.GetValue(null)!;
        var contextType = typeof(TerminalRunner).GetNestedType("PostStepContext", BindingFlags.NonPublic)!;
        var id = "post-steps-" + Guid.NewGuid().ToString("N");
        contexts[id] = Activator.CreateInstance(contextType, 17, "env", "/project")!;

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<StepRunSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var steps = new Mock<IEnvironmentStepRunner>();
        steps.Setup(s => s.RunPhaseAsync(17, EnvironmentStepPhase.PostExit, "/project", null, It.IsAny<CancellationToken>()))
            .Returns(() => { entered.TrySetResult(); return release.Task; });
        var runner = new TerminalRunner(Mock.Of<ITerminalStateService>(), Mock.Of<ICommandService>(), Mock.Of<ILocalToolApiContext>(),
            Mock.Of<ILlmProxySessionState>(), Mock.Of<IAutomationConsumer>(), Mock.Of<IRepository>(), steps.Object, new AppEventBus());
        var ct = TestContext.Current.CancellationToken;

        var first = runner.RunPostStepsAsync(id, 0, ct);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        var second = runner.RunPostStepsAsync(id, 0, ct);
        await Task.Delay(50, ct);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted, "a caller arriving while the run is in flight must wait for it");

        release.SetResult(StepRunSummary.Empty);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), ct);
        steps.Verify(s => s.RunPhaseAsync(17, EnvironmentStepPhase.PostExit, "/project", null, It.IsAny<CancellationToken>()), Times.Once);

        // After the run has finished, a late caller is a plain no-op.
        await runner.RunPostStepsAsync(id, 0, ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        steps.VerifyNoOtherCalls();
    }
}
