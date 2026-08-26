using Moq;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.VCA;

public sealed class VcaHookRunnerAutomationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunAsync_OnlyExplicitHookAuthorizationEnablesAutomationEnqueue(bool authorized)
    {
        var pipeline = new CapturingPipeline();
        var runner = new VcaHookRunner(
            pipeline,
            new VcaHookValidationAnalyzer(),
            Mock.Of<IVcaHookPresenter>(),
            Mock.Of<ICommitMessageCoAuthorCleaner>());

        var exitCode = await runner.RunAsync(
            new VcaHookInvocation(
                VcaHookKind.PreCommit,
                CommitMessagePath: null,
                WorkingDirectory: Directory.GetCurrentDirectory(),
                DemoUi: false,
                DemoDuration: TimeSpan.Zero,
                PromptForAcknowledgment: false,
                EnqueueAutomatedJobs: authorized),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.Equal(authorized, pipeline.Request?.EnqueueAutomatedJobs);
    }

    private sealed class CapturingPipeline : IGitPreflightPipeline
    {
        public GitPreflightRequest? Request { get; private set; }

        public Task<GitPreflightRunResult> RunAsync(
            GitPreflightRequest request,
            Func<GitPreflightEvent, CancellationToken, ValueTask>? eventSink = null,
            CancellationToken cancellationToken = default)
        {
            Request = request;
            var summary = new VcaHookValidationSummary(
                HasError: false,
                HasStopViolation: false,
                HasCommitViolations: false,
                RequiredAcknowledgments: []);
            return Task.FromResult(new GitPreflightRunResult(
                "run",
                request.WorkingDirectory,
                [],
                [new GitPreflightStepResult(
                    VcaPreflightStep.Id,
                    "VCA",
                    GitPreflightStepStatus.Passed,
                    "Passed",
                    ["Passed"],
                    DurationMs: 0,
                    Blocking: true,
                    VcaSummary: summary)],
                GitPreflightStepStatus.Passed,
                CommitAllowed: true,
                DurationMs: 0));
        }
    }
}
