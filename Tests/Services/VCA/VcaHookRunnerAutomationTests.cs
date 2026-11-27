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
        // The hook prints step output and keeps nothing, so it never asks for the MintLint report.
        Assert.False(pipeline.Request?.IncludeReport);
    }

    // VIBE-327: without a chained hook the script runs no separate cleanup pass, so the commit-msg
    // launch cleans the message itself. Like that pass, a cleanup failure never blocks the commit.
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task RunAsync_CommitMessageCleansInlineUnlessTheScriptAlreadyDidAndNeverBlocksOnIt(bool alreadyCleaned, bool cleanupFails)
    {
        var pipeline = new CapturingPipeline();
        var presenter = new Mock<IVcaHookPresenter>();
        var cleaner = new Mock<ICommitMessageCoAuthorCleaner>();
        var setup = cleaner.Setup(c => c.RemoveAsync("COMMIT_EDITMSG", It.IsAny<CancellationToken>()));
        if (cleanupFails) setup.ThrowsAsync(new IOException("locked"));
        else setup.ReturnsAsync(1);
        var runner = new VcaHookRunner(pipeline, new VcaHookValidationAnalyzer(), presenter.Object, cleaner.Object);

        var exitCode = await runner.RunAsync(
            new VcaHookInvocation(
                VcaHookKind.CommitMessage,
                CommitMessagePath: "COMMIT_EDITMSG",
                WorkingDirectory: Directory.GetCurrentDirectory(),
                DemoUi: false,
                DemoDuration: TimeSpan.Zero,
                PromptForAcknowledgment: false,
                CoAuthorsAlreadyCleaned: alreadyCleaned),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, exitCode);
        Assert.NotNull(pipeline.Request);
        cleaner.Verify(c => c.RemoveAsync("COMMIT_EDITMSG", It.IsAny<CancellationToken>()), alreadyCleaned ? Times.Never() : Times.Once());
        if (!alreadyCleaned)
            presenter.Verify(p => p.WriteWarningAsync(It.Is<string>(text => text.Contains(cleanupFails
                ? "could not apply commit-message cleanup" : "Removed 1 trailer"))), Times.Once());
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
