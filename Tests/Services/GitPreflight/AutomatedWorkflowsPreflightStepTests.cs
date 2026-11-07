using Moq;
using VibeRails.DB;
using VibeRails.Data.Sqlite;
using VibeRails.Launches;
using VibeRails.Services.Launches;
using VibeRails.Services.Jobs;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.GitPreflight;

public sealed class AutomatedWorkflowsPreflightStepTests
{
    [Theory]
    [InlineData(true, JobTriggerKind.PreCommit, 1)]
    [InlineData(false, JobTriggerKind.PreCommit, 0)]
    [InlineData(true, JobTriggerKind.Commit, 0)]
    public async Task OnlyMatchingAuthorizedEventsQueueSingleSnapshots(bool enqueue, JobTriggerKind trigger, int count)
    {
        var root = Path.Combine(Path.GetTempPath(), "launch-preflight-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Snapshot.RepositoryPath);
        try
        {
            var store = new LaunchStore($"Data Source={Path.Combine(root, "launches.db")};Pooling=False");
            var service = new LaunchService(store, Mock.Of<IAutomationScriptService>());
            var preset = (await service.SavePresetAsync(null, new("Review", "", "codex", InitialPrompt: "Review"), TestContext.Current.CancellationToken)).Preset;
            await service.SaveBindingAsync(Snapshot.RepositoryPath, null, new(preset.Id, "preset", new(trigger)), TestContext.Current.CancellationToken);
            var step = new AutomatedWorkflowsPreflightStep(launches: new LaunchEventService(store, service));
            var (result, _) = await ExecuteAsync(step, Request(enqueue));
            var runs = await store.ListRunsAsync(LaunchService.ProjectKey(Snapshot.RepositoryPath), ct: TestContext.Current.CancellationToken);
            Assert.Equal(count, runs.Runs.Count);
            Assert.False(result.Blocking);
            await ExecuteAsync(step, Request(enqueue));
            Assert.Equal(count, (await store.ListRunsAsync(LaunchService.ProjectKey(Snapshot.RepositoryPath), ct: TestContext.Current.CancellationToken)).Runs.Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RetiredAccessorIsNeverOpened()
    {
        var store = new Mock<IJobStore>(MockBehavior.Strict);
        var (result, output) = await ExecuteAsync(Step(store.Object), Request(true));
        Assert.Equal(GitPreflightStepStatus.Skipped, result.Status);
        Assert.Contains("retired", output);
        store.VerifyNoOtherCalls();
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
