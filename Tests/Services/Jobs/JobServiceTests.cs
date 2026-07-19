using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
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
        _service = new JobService(_store, _repository.Object, _resolver.Object, _supervisor.Object);
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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
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
