using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.GitPreflight;

public sealed class AutomatedWorkflowsPreflightStepTests
{
    [Fact]
    public async Task ExecuteAsync_NativePreCommit_QueuesMatchingAutomations()
    {
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        store
            .Setup(item => item.GetJobsAsync(Snapshot.RepositoryPath, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Job(enabled: true, JobTriggerKind.PreCommit)]);
        store
            .Setup(item => item.EnqueueEventRunsAsync(
                Snapshot.RepositoryPath,
                JobTriggerKind.PreCommit,
                "run-1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(["queued-run"]);

        var (result, output) = await ExecuteAsync(
            Step(store.Object),
            Request(enqueue: true));

        Assert.Equal(GitPreflightStepStatus.Passed, result.Status);
        Assert.False(result.Blocking);
        Assert.Contains("Queued 1 automation", output);
        store.Verify(item => item.EnqueueEventRunsAsync(
            Snapshot.RepositoryPath,
            JobTriggerKind.PreCommit,
            "run-1",
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_GitGuardPreview_DoesNotEnqueue()
    {
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        store
            .Setup(item => item.GetJobsAsync(Snapshot.RepositoryPath, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Job(enabled: true, JobTriggerKind.PreCommit)]);

        var (result, output) = await ExecuteAsync(
            Step(store.Object),
            Request(enqueue: false));

        Assert.Equal(GitPreflightStepStatus.Skipped, result.Status);
        Assert.Contains("does not start", output, StringComparison.OrdinalIgnoreCase);
        store.Verify(
            item => item.EnqueueEventRunsAsync(
                It.IsAny<string>(),
                It.IsAny<JobTriggerKind>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_IgnoresAfterCommitAutomations()
    {
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        store
            .Setup(item => item.GetJobsAsync(Snapshot.RepositoryPath, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync([Job(enabled: true, JobTriggerKind.Commit)]);

        var (result, output) = await ExecuteAsync(
            Step(store.Object),
            Request(enqueue: true));

        Assert.Equal(GitPreflightStepStatus.Skipped, result.Status);
        Assert.Contains("No automations", output, StringComparison.OrdinalIgnoreCase);
        store.Verify(
            item => item.EnqueueEventRunsAsync(
                It.IsAny<string>(),
                It.IsAny<JobTriggerKind>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_SkipsEnqueueWhenVcaBlockedTheCommit()
    {
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        var vca = new GitPreflightStepResult(
            VcaPreflightStep.Id,
            "VCA",
            GitPreflightStepStatus.Blocked,
            "blocked",
            ["blocked"],
            0,
            true);

        var (result, output) = await ExecuteAsync(
            Step(store.Object),
            Request(enqueue: true),
            [vca]);

        Assert.Equal(GitPreflightStepStatus.Skipped, result.Status);
        Assert.Contains("VCA blocked", output, StringComparison.OrdinalIgnoreCase);
        store.Verify(
            item => item.GetJobsAsync(It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
        store.Verify(
            item => item.EnqueueEventRunsAsync(
                It.IsAny<string>(),
                It.IsAny<JobTriggerKind>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_CommitMessageHook_DoesNotLookUpJobs()
    {
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        var request = Request(enqueue: true) with
        {
            Invocation = Request(enqueue: true).Invocation with { Kind = VcaHookKind.CommitMessage }
        };

        var (result, output) = await ExecuteAsync(
            Step(store.Object),
            request);

        Assert.Equal(GitPreflightStepStatus.Skipped, result.Status);
        Assert.DoesNotContain("Queued", output, StringComparison.OrdinalIgnoreCase);
        store.VerifyNoOtherCalls();
    }

    private static async Task<(GitPreflightStepResult Result, string Output)> ExecuteAsync(
        AutomatedWorkflowsPreflightStep step,
        GitPreflightRequest request,
        IReadOnlyList<GitPreflightStepResult>? completed = null)
    {
        var output = new List<string>();
        var result = await step.ExecuteAsync(
            new GitPreflightStepContext(
                "run-1",
                request,
                Snapshot,
                (message, _, _) =>
                {
                    output.Add(message);
                    return ValueTask.CompletedTask;
                },
                completed),
            TestContext.Current.CancellationToken);
        return (result, Assert.Single(output));
    }

    private static GitPreflightRequest Request(bool enqueue) => new(
        Snapshot.RepositoryPath,
        new VcaHookInvocation(
            VcaHookKind.PreCommit,
            null,
            Snapshot.RepositoryPath,
            DemoUi: false,
            DemoDuration: TimeSpan.Zero,
            PromptForAcknowledgment: false),
        EnqueueAutomatedJobs: enqueue);

    private static AutomatedWorkflowsPreflightStep Step(IJobStore store) =>
        new(new FixedJobStoreAccessor(store));

    private static GitStagedSnapshot Snapshot { get; } = new(
        Path.Combine(Path.GetTempPath(), "precommit-automations-repo"),
        [],
        []);

    private static JobDefinitionRecord Job(bool enabled, params JobTriggerKind[] kinds) => new(
        1,
        "review",
        Snapshot.RepositoryPath,
        LLM.Claude,
        7,
        "review-worker",
        "review the diff",
        30,
        enabled,
        DateTime.UtcNow,
        DateTime.UtcNow,
        null,
        kinds.Select(kind => new JobTriggerDto(1, kind)).ToList());

    private sealed class FixedJobStoreAccessor(IJobStore store) : IJobStoreAccessor
    {
        public IJobStore GetRequiredStore() => store;
    }
}
