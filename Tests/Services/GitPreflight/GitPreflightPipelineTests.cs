using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.GitPreflight;

public sealed class GitPreflightPipelineTests
{
    [Fact]
    public async Task RunAsync_UsesFixedOrder_AndOnlyVcaCanBlock()
    {
        var executionOrder = new List<string>();
        var events = new List<GitPreflightEvent>();
        var pipeline = Pipeline(
            executionOrder,
            GitPreflightStepStatus.Passed,
            GitPreflightStepStatus.Error);

        var result = await pipeline.RunAsync(
            Request(),
            (item, _) =>
            {
                events.Add(item);
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [VcaPreflightStep.Id, MintLintPreflightStep.Id, AutomatedWorkflowsPreflightStep.Id],
            executionOrder);
        Assert.True(result.CommitAllowed);
        Assert.Equal(GitPreflightStepStatus.Warning, result.Status);
        Assert.Equal(
            Enumerable.Range(1, events.Count).Select(value => (long)value),
            events.Select(item => item.Sequence));
        Assert.Equal(GitPreflightEventType.RunStarted, events.First().Type);
        Assert.Equal(GitPreflightEventType.RunFinished, events.Last().Type);
    }

    [Fact]
    public async Task RunAsync_BlocksWhenVcaBlocks_ButStillRunsReportOnlySteps()
    {
        var executionOrder = new List<string>();
        var pipeline = Pipeline(
            executionOrder,
            GitPreflightStepStatus.Blocked,
            GitPreflightStepStatus.Passed);

        var result = await pipeline.RunAsync(
            Request(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.CommitAllowed);
        Assert.Equal(GitPreflightStepStatus.Blocked, result.Status);
        Assert.Equal(3, result.Steps.Count);
        Assert.Equal(3, executionOrder.Count);
    }

    [Fact]
    public async Task RunAsync_ReturnsCancelledResult_WhenStepObservesCancellation()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pipeline = new GitPreflightPipeline(
            new SnapshotProvider(),
            [
                new CancellingStep(cts),
                new TestStep(MintLintPreflightStep.Id, false, GitPreflightStepStatus.Passed, []),
                new TestStep(AutomatedWorkflowsPreflightStep.Id, false, GitPreflightStepStatus.Skipped, [])
            ]);

        var result = await pipeline.RunAsync(Request(), cancellationToken: cts.Token);

        Assert.Equal(GitPreflightStepStatus.Cancelled, result.Status);
        Assert.False(result.CommitAllowed);
    }

    [Fact]
    public async Task AutomatedWorkflowStep_PrintsExactPlaceholder()
    {
        var output = new List<string>();
        var result = await new AutomatedWorkflowsPreflightStep().ExecuteAsync(
            new GitPreflightStepContext(
                "run",
                Request(),
                SnapshotProvider.Snapshot,
                (message, _, _) =>
                {
                    output.Add(message);
                    return ValueTask.CompletedTask;
                }),
            TestContext.Current.CancellationToken);

        Assert.Equal("Placeholder for automated workflows", Assert.Single(output));
        Assert.Equal(GitPreflightStepStatus.Skipped, result.Status);
    }

    [Fact]
    public async Task AutomatedWorkflowStep_CommitMessage_DoesNotRepeatPlaceholder()
    {
        var messages = new List<string>();
        var request = Request() with
        {
            Invocation = Request().Invocation with { Kind = VcaHookKind.CommitMessage }
        };
        await new AutomatedWorkflowsPreflightStep().ExecuteAsync(
            new GitPreflightStepContext(
                "run",
                request,
                SnapshotProvider.Snapshot,
                (message, _, _) =>
                {
                    messages.Add(message);
                    return ValueTask.CompletedTask;
                }),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(AutomatedWorkflowsPreflightStep.PlaceholderMessage, messages);
    }

    private static GitPreflightPipeline Pipeline(
        List<string> order,
        GitPreflightStepStatus vcaStatus,
        GitPreflightStepStatus mintLintStatus) =>
        new(
            new SnapshotProvider(),
            [
                new TestStep(AutomatedWorkflowsPreflightStep.Id, false, GitPreflightStepStatus.Skipped, order),
                new TestStep(MintLintPreflightStep.Id, false, mintLintStatus, order),
                new TestStep(VcaPreflightStep.Id, true, vcaStatus, order)
            ]);

    private static GitPreflightRequest Request() => new(
        Directory.GetCurrentDirectory(),
        new VcaHookInvocation(
            VcaHookKind.PreCommit,
            null,
            Directory.GetCurrentDirectory(),
            DemoUi: false,
            DemoDuration: TimeSpan.Zero,
            PromptForAcknowledgment: false));

    private sealed class SnapshotProvider : IGitStagedSnapshotProvider
    {
        public static GitStagedSnapshot Snapshot { get; } = new(
            Directory.GetCurrentDirectory(),
            [new GitStagedFileSnapshot(
                "demo.cs",
                Path.Combine(Directory.GetCurrentDirectory(), "demo.cs"),
                GitStagedChangeKind.Modified,
                true,
                false,
                1,
                "class Demo { }")],
            []);

        public Task<GitStagedSnapshot> CaptureAsync(string workingDirectory, CancellationToken cancellationToken) =>
            Task.FromResult(Snapshot);
    }

    private sealed class TestStep : IGitPreflightStep
    {
        private readonly GitPreflightStepStatus _status;
        private readonly List<string> _order;

        public TestStep(string id, bool canBlock, GitPreflightStepStatus status, List<string> order)
        {
            StepId = id;
            DisplayName = id;
            CanBlock = canBlock;
            _status = status;
            _order = order;
        }

        public string StepId { get; }
        public string DisplayName { get; }
        public bool CanBlock { get; }

        public async Task<GitPreflightStepResult> ExecuteAsync(
            GitPreflightStepContext context,
            CancellationToken cancellationToken)
        {
            _order.Add(StepId);
            await context.WriteOutputAsync(StepId, null, cancellationToken);
            return new GitPreflightStepResult(
                StepId,
                DisplayName,
                _status,
                StepId,
                [StepId],
                0,
                CanBlock,
                VcaSummary: StepId == VcaPreflightStep.Id
                    ? new VcaHookValidationSummary(false, _status == GitPreflightStepStatus.Blocked, false, [])
                    : null);
        }
    }

    private sealed class CancellingStep : IGitPreflightStep
    {
        private readonly CancellationTokenSource _cts;

        public CancellingStep(CancellationTokenSource cts)
        {
            _cts = cts;
        }

        public string StepId => VcaPreflightStep.Id;
        public string DisplayName => "VCA";
        public bool CanBlock => true;

        public Task<GitPreflightStepResult> ExecuteAsync(
            GitPreflightStepContext context,
            CancellationToken cancellationToken)
        {
            _cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unreachable");
        }
    }
}
