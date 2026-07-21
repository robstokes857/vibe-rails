using Microsoft.Data.Sqlite;
using Moq;
using System.Text.Json;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

public sealed class JobServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"viberails_job_service_{Guid.NewGuid():N}");
    private readonly JobStore _store;
    private readonly Mock<IRepository> _repository = new();
    private readonly Mock<IJobExecutableResolver> _resolver = new();
    private readonly Mock<IJobWorkerSupervisor> _supervisor = new();
    private readonly JobWorkspaceService _workspaceService;
    private readonly JobService _service;

    public JobServiceTests()
    {
        Directory.CreateDirectory(_directory);
        var connectionString = $"Data Source={Path.Combine(_directory, "state.db")};Mode=ReadWriteCreate;Cache=Shared";
        CreateEnvironmentTable(connectionString);
        _store = new JobStore(connectionString);
        _supervisor
            .Setup(supervisor => supervisor.EnsureInstalledAndRunningAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _workspaceService = new JobWorkspaceService(Path.Combine(_directory, "job-artifacts"));
        _service = new JobService(
            _store,
            _repository.Object,
            _resolver.Object,
            _supervisor.Object,
            _workspaceService);
    }

    [Fact]
    public async Task CreateJobAsync_DisabledJobDoesNotRequireCliOrStartWorker()
    {
        await InitializeRepositoryAsync();

        var created = await _service.CreateJobAsync(
            Request(enabled: false) with { Name = "  Review  ", Prompt = "  Check this.  " },
            TestContext.Current.CancellationToken);

        Assert.Equal("Review", created.Name);
        Assert.Equal("Check this.", created.Prompt);
        Assert.False(created.Enabled);
        _supervisor.Verify(
            supervisor => supervisor.EnsureInstalledAndRunningAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateJobAsync_EnabledJobRequiresInstalledCli()
    {
        await InitializeRepositoryAsync();

        var error = await Assert.ThrowsAsync<JobServiceException>(() =>
            _service.CreateJobAsync(Request(enabled: true), TestContext.Current.CancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("Codex CLI was not found", error.Message, StringComparison.Ordinal);
        _supervisor.Verify(
            supervisor => supervisor.EnsureInstalledAndRunningAsync(It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CreateJobAsync_EnabledJobStartsWorkerAndPersistsTrigger()
    {
        await InitializeRepositoryAsync();
        _resolver.Setup(resolver => resolver.Resolve(LLM.Codex)).Returns("codex");

        var created = await _service.CreateJobAsync(
            Request(enabled: true) with
            {
                Triggers = [new JobTriggerRequest(JobTriggerKind.Vca)]
            },
            TestContext.Current.CancellationToken);

        Assert.True(created.Enabled);
        Assert.Equal(JobTriggerKind.Vca, Assert.Single(created.Triggers).Kind);
        _supervisor.Verify(
            supervisor => supervisor.EnsureInstalledAndRunningAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CreateJobAsync_RejectsNestedDirectoryInsteadOfSilentlyChangingScope()
    {
        await InitializeRepositoryAsync();
        var nested = Directory.CreateDirectory(Path.Combine(_directory, "nested")).FullName;

        var error = await Assert.ThrowsAsync<JobServiceException>(() =>
            _service.CreateJobAsync(
                Request(enabled: false) with { ProjectPath = nested },
                TestContext.Current.CancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("must be the Git repository root", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateJobAsync_RejectsUnsupportedLlmAndMalformedSchedule()
    {
        await InitializeRepositoryAsync();

        var unsupported = await Assert.ThrowsAsync<JobServiceException>(() =>
            _service.CreateJobAsync(
                Request(enabled: false) with { Llm = LLM.Antigravity },
                TestContext.Current.CancellationToken));
        var malformedSchedule = await Assert.ThrowsAsync<JobServiceException>(() =>
            _service.CreateJobAsync(
                Request(enabled: false) with
                {
                    Triggers = [new JobTriggerRequest(
                        JobTriggerKind.Schedule,
                        JobScheduleKind.Interval,
                        IntervalMinutes: 1)]
                },
                TestContext.Current.CancellationToken));

        Assert.Equal(400, unsupported.StatusCode);
        Assert.Contains("only Codex and Claude", unsupported.Message, StringComparison.Ordinal);
        Assert.Equal(400, malformedSchedule.StatusCode);
        Assert.Contains("Interval must be", malformedSchedule.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryRunAsync_RecreatesAndPublishesFreshStagedSnapshot()
    {
        await InitializeCommittedRepositoryAsync();
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "tracked.cs"),
            "class StagedForRetry { }\n",
            TestContext.Current.CancellationToken);
        await GitAsync("add", "tracked.cs");
        var staged = await new GitStagedSnapshotProvider().CaptureAsync(
            _directory,
            TestContext.Current.CancellationToken);
        using var originalCapture = await _workspaceService.CaptureStagedSnapshotAsync(
            staged,
            TestContext.Current.CancellationToken);
        var originalContext = SnapshotContext(originalCapture);

        var job = await _store.CreateJobAsync(
            Request(enabled: false),
            "codex",
            TestContext.Current.CancellationToken);
        var sourceId = await _store.EnqueueManualRunAsync(
            job.Id,
            originalContext,
            TestContext.Current.CancellationToken);
        Assert.NotNull(sourceId);
        await _workspaceService.MaterializeRunSnapshotsAsync(
            originalCapture,
            [sourceId!],
            TestContext.Current.CancellationToken);
        originalCapture.Dispose();
        var source = await _store.ClaimNextRunAsync(
            "worker",
            42,
            TestContext.Current.CancellationToken);
        Assert.NotNull(source);
        _workspaceService.Cleanup(source, workspace: null);
        await _store.CompleteRunAsync(
            source.Id,
            JobRunStatus.Failed,
            1,
            null,
            "failed",
            cancellationToken: TestContext.Current.CancellationToken);

        _resolver.Setup(resolver => resolver.Resolve(LLM.Codex)).Returns("codex");
        var result = await _service.RetryRunAsync(
            source.Id,
            TestContext.Current.CancellationToken);

        var retry = await _store.GetRunAsync(result.RunId!, TestContext.Current.CancellationToken);
        Assert.NotNull(retry);
        using var retryContext = JsonDocument.Parse(retry.TriggerContextJson!);
        var retrySnapshotId = retryContext.RootElement
            .GetProperty(JobWorkspaceService.StagedSnapshotIdContextKey)
            .GetString();
        Assert.NotEqual(originalCapture.Id, retrySnapshotId);
        Assert.Single(Directory.EnumerateFiles(
            Path.Combine(_directory, "job-artifacts"),
            "*.staged.patch"));

        var workspace = await _workspaceService.PrepareAsync(
            retry,
            TestContext.Current.CancellationToken);
        try
        {
            var content = await File.ReadAllTextAsync(
                Path.Combine(workspace, "tracked.cs"),
                TestContext.Current.CancellationToken);
            Assert.Equal("class StagedForRetry { }\n", content.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            _workspaceService.Cleanup(retry, workspace);
        }
    }

    [Fact]
    public async Task RetryRunAsync_UnavailableGitObjectsFailsWithoutQueueing()
    {
        await InitializeRepositoryAsync();
        var job = await _store.CreateJobAsync(
            Request(enabled: false),
            "codex",
            TestContext.Current.CancellationToken);
        var unavailableContext = JsonSerializer.Serialize(new Dictionary<string, string>
        {
            [JobWorkspaceService.StagedSnapshotIdContextKey] = Guid.NewGuid().ToString("N"),
            [JobWorkspaceService.StagedSnapshotBaseTreeContextKey] = new string('0', 40),
            [JobWorkspaceService.StagedSnapshotTreeContextKey] = new string('1', 40)
        });
        var sourceId = await _store.EnqueueManualRunAsync(
            job.Id,
            unavailableContext,
            TestContext.Current.CancellationToken);
        Assert.NotNull(sourceId);
        var source = await _store.ClaimNextRunAsync(
            "worker",
            42,
            TestContext.Current.CancellationToken);
        Assert.NotNull(source);
        await _store.CompleteRunAsync(
            source.Id,
            JobRunStatus.Failed,
            1,
            null,
            "failed",
            cancellationToken: TestContext.Current.CancellationToken);
        _resolver.Setup(resolver => resolver.Resolve(LLM.Codex)).Returns("codex");

        var error = await Assert.ThrowsAsync<JobServiceException>(() =>
            _service.RetryRunAsync(source.Id, TestContext.Current.CancellationToken));

        Assert.Equal(409, error.StatusCode);
        Assert.Contains("no retry was queued", error.Message, StringComparison.Ordinal);
        Assert.Single(await _store.GetRunsAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.Empty(Directory.Exists(Path.Combine(_directory, "job-artifacts"))
            ? Directory.EnumerateFiles(Path.Combine(_directory, "job-artifacts"), "*.patch")
            : []);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_directory, recursive: true);
        }
    }

    private CreateJobRequest Request(bool enabled) => new(
        "Review",
        _directory,
        LLM.Codex,
        EnvironmentId: null,
        Prompt: "Check this.",
        JobExecutionMode.Review,
        TimeoutMinutes: 30,
        Enabled: enabled,
        Triggers: []);

    private async Task InitializeRepositoryAsync()
    {
        var result = await GitProcessRunner.RunAsync(
            ["init"],
            _directory,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        Assert.Equal(0, result.ExitCode);
    }

    private async Task InitializeCommittedRepositoryAsync()
    {
        await InitializeRepositoryAsync();
        await GitAsync("config", "user.email", "tests@viberails.local");
        await GitAsync("config", "user.name", "VibeRails Tests");
        await File.WriteAllTextAsync(
            Path.Combine(_directory, "tracked.cs"),
            "class Original { }\n",
            TestContext.Current.CancellationToken);
        await GitAsync("add", "tracked.cs");
        await GitAsync("commit", "-m", "initial");
    }

    private async Task GitAsync(params string[] arguments)
    {
        var result = await GitProcessRunner.RunAsync(
            arguments,
            _directory,
            TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);
        Assert.True(result.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {result.StdErr}");
    }

    private static string SnapshotContext(JobWorkspaceService.CapturedStagedSnapshot capture)
    {
        var context = new Dictionary<string, string>
        {
            [JobWorkspaceService.StagedSnapshotIdContextKey] = capture.Id,
            [JobWorkspaceService.StagedSnapshotBaseTreeContextKey] = capture.BaseTree,
            [JobWorkspaceService.StagedSnapshotTreeContextKey] = capture.StagedTree
        };
        if (capture.BaseCommit is not null)
            context[JobWorkspaceService.StagedSnapshotBaseCommitContextKey] = capture.BaseCommit;
        return JsonSerializer.Serialize(context);
    }

    private static void CreateEnvironmentTable(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Environments (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                CustomName TEXT NOT NULL,
                LLM INTEGER NOT NULL,
                Path TEXT NOT NULL DEFAULT '',
                CustomArgs TEXT NOT NULL DEFAULT '',
                CustomPrompt TEXT NOT NULL DEFAULT '',
                CreatedUTC TEXT NOT NULL,
                LastUsedUTC TEXT NOT NULL,
                UNIQUE(CustomName, LLM)
            );
            """;
        command.ExecuteNonQuery();
    }
}
