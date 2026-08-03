using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Jobs;

/// <summary>
/// JobService is the only thing standing between a request and the Jobs tables, and its rules are
/// the ones a user actually hits: a worker with no initial message, a path that isn't a repo root, a
/// timeout outside the allowed range. The E2E suite mocks the Jobs API wholesale, so if these are
/// not covered here they are not covered anywhere.
///
/// Uses a real throwaway git repository rather than a mocked path check, because ValidateProjectAsync
/// shells out to `git rev-parse --show-toplevel` — a fake path would exercise none of it.
/// </summary>
public sealed class JobServiceTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly Mock<IJobStore> _store = new();
    private readonly Mock<IRepository> _repository = new();
    private readonly Mock<IJobExecutableResolver> _executableResolver = new();
    private readonly Mock<IJobScheduler> _scheduler = new();

    public JobServiceTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), $"vb-jobsvc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoRoot);
        RunGit("init");
        // GetFullPath resolves any 8.3 or symlinked form of the temp path so it compares equal to
        // what git reports back as the toplevel.
        _repoRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_repoRoot));
    }

    [Fact]
    public async Task CreateJob_WithoutAnEnvironment_IsRejected()
    {
        var error = await Assert.ThrowsAsync<JobServiceException>(() =>
            Service().CreateJobAsync(Request(environmentId: null), TestContext.Current.CancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("Environment", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task CreateJob_WithBlankName_IsRejected(string name)
    {
        WithEnvironment();

        var error = await Assert.ThrowsAsync<JobServiceException>(() =>
            Service().CreateJobAsync(Request(name: name), TestContext.Current.CancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("Automation name is required", error.Message);
    }

    [Fact]
    public async Task CreateJob_WithNameOverTheLimit_IsRejected()
    {
        WithEnvironment();

        var error = await Assert.ThrowsAsync<JobServiceException>(() =>
            Service().CreateJobAsync(Request(name: new string('n', 101)), TestContext.Current.CancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("100 characters", error.Message);
    }

    [Fact]
    public async Task CreateJob_WithNameExactlyAtTheLimit_IsAccepted()
    {
        WithEnvironment();
        WithSuccessfulCreate();

        var created = await Service().CreateJobAsync(
            Request(name: new string('n', 100)), TestContext.Current.CancellationToken);

        Assert.NotNull(created);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(721)]
    public async Task CreateJob_WithTimeoutOutsideTheAllowedRange_IsRejected(int timeoutMinutes)
    {
        WithEnvironment();

        var error = await Assert.ThrowsAsync<JobServiceException>(() =>
            Service().CreateJobAsync(Request(timeoutMinutes: timeoutMinutes), TestContext.Current.CancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("1–720 minutes", error.Message);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(720)]
    [InlineData(null)]
    public async Task CreateJob_WithTimeoutInsideTheRangeOrOmitted_IsAccepted(int? timeoutMinutes)
    {
        WithEnvironment();
        WithSuccessfulCreate();

        // null is the default and means "no limit" — it must not be validated as out of range.
        var created = await Service().CreateJobAsync(
            Request(timeoutMinutes: timeoutMinutes), TestContext.Current.CancellationToken);

        Assert.NotNull(created);
    }

    [Fact]
    public async Task CreateJob_WhenTheWorkerHasNoInitialMessage_IsRejected()
    {
        // The worker is the sole source of the prompt, so one without an initial message would run
        // the CLI with nothing to do.
        WithEnvironment(customPrompt: "   ");

        var error = await Assert.ThrowsAsync<JobServiceException>(() =>
            Service().CreateJobAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("Initial Message", error.Message);
    }

    [Fact]
    public async Task CreateJob_WhenTheWorkerNoLongerExists_IsRejected()
    {
        _repository
            .Setup(r => r.GetAllEnvironmentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var error = await Assert.ThrowsAsync<JobServiceException>(() =>
            Service().CreateJobAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("no longer exists", error.Message);
    }

    [Fact]
    public async Task CreateJob_WithAPathThatIsNotARepositoryRoot_IsRejected()
    {
        WithEnvironment();
        var nested = Path.Combine(_repoRoot, "src");
        Directory.CreateDirectory(nested);

        var error = await Assert.ThrowsAsync<JobServiceException>(() =>
            Service().CreateJobAsync(Request(projectPath: nested), TestContext.Current.CancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("Git repository root", error.Message);
    }

    [Fact]
    public async Task CreateJob_WithAPathOutsideAnyRepository_IsRejected()
    {
        WithEnvironment();
        var loose = Path.Combine(Path.GetTempPath(), $"vb-nogit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(loose);

        try
        {
            var error = await Assert.ThrowsAsync<JobServiceException>(() =>
                Service().CreateJobAsync(Request(projectPath: loose), TestContext.Current.CancellationToken));

            Assert.Equal(400, error.StatusCode);
            Assert.Contains("Git repository", error.Message);
        }
        finally
        {
            Directory.Delete(loose, recursive: true);
        }
    }

    [Fact]
    public async Task CreateJob_WithAMissingDirectory_IsRejected()
    {
        WithEnvironment();

        var error = await Assert.ThrowsAsync<JobServiceException>(() =>
            Service().CreateJobAsync(
                Request(projectPath: Path.Combine(_repoRoot, "does-not-exist")),
                TestContext.Current.CancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("does not exist", error.Message);
    }

    [Fact]
    public async Task CreateJob_WithTwoTriggersOfTheSameKind_IsRejected()
    {
        WithEnvironment();

        var error = await Assert.ThrowsAsync<JobServiceException>(() =>
            Service().CreateJobAsync(
                Request(triggers:
                [
                    new JobTriggerRequest(JobTriggerKind.Commit),
                    new JobTriggerRequest(JobTriggerKind.Commit)
                ]),
                TestContext.Current.CancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("Only one", error.Message);
    }

    [Fact]
    public async Task CreateJob_WithAShellWorker_IsRejected()
    {
        // A plain shell has no agent to give a prompt to, so it cannot be automated.
        WithEnvironment(llm: LLM.Shell);

        var error = await Assert.ThrowsAsync<JobServiceException>(() =>
            Service().CreateJobAsync(Request(), TestContext.Current.CancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("cannot run as an Automation", error.Message);
    }

    [Fact]
    public async Task CreateJob_TakesTheClIAndPromptFromTheWorker_NotTheRequest()
    {
        // The Job owns *when* it runs; the worker owns everything about *what* runs. A request that
        // disagrees must not be able to smuggle in a different CLI or prompt.
        WithEnvironment(llm: LLM.Codex, customPrompt: "  review the diff  ");
        CreateJobRequest? captured = null;
        _store
            .Setup(s => s.CreateJobAsync(It.IsAny<CreateJobRequest>(), It.IsAny<CancellationToken>()))
            .Callback<CreateJobRequest, CancellationToken>((request, _) => captured = request)
            .ReturnsAsync(Record());

        await Service().CreateJobAsync(
            Request(llm: LLM.Claude, prompt: "something else"), TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(LLM.Codex, captured.Llm);
        Assert.Equal("review the diff", captured.Prompt);
    }

    [Fact]
    public async Task UpdateJob_CannotDetachAnExistingWorker()
    {
        _store
            .Setup(s => s.GetJobAsync(7, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Record() with { EnvironmentId = 1 });

        var error = await Assert.ThrowsAsync<JobServiceException>(() =>
            Service().UpdateJobAsync(
                7,
                new UpdateJobRequest("nightly", _repoRoot, LLM.Claude, null, "", null, true, []),
                TestContext.Current.CancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("cannot be detached", error.Message);
    }

    [Fact]
    public async Task DeleteRuns_RejectsMoreThanOneBoundedPageOfIds()
    {
        var ids = Enumerable.Range(0, 101).Select(index => $"run-{index}").ToList();

        var error = await Assert.ThrowsAsync<JobServiceException>(() =>
            Service().DeleteRunsAsync(ids, TestContext.Current.CancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("at most 100", error.Message, StringComparison.OrdinalIgnoreCase);
        _store.Verify(
            store => store.SoftDeleteRunsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task DeleteRuns_RejectsAnOversizedRunId()
    {
        var error = await Assert.ThrowsAsync<JobServiceException>(() =>
            Service().DeleteRunsAsync([new string('r', 129)], TestContext.Current.CancellationToken));

        Assert.Equal(400, error.StatusCode);
        Assert.Contains("too long", error.Message, StringComparison.OrdinalIgnoreCase);
        _store.Verify(
            store => store.SoftDeleteRunsAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    private JobService Service() => new(
        _store.Object, _repository.Object, _executableResolver.Object, _scheduler.Object);

    private void WithEnvironment(
        int id = 1,
        LLM llm = LLM.Claude,
        string customPrompt = "run the nightly review")
    {
        _repository
            .Setup(r => r.GetAllEnvironmentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                new LLM_Environment
                {
                    Id = id,
                    LLM = llm,
                    CustomName = "nightly",
                    CustomArgs = "",
                    CustomPrompt = customPrompt
                }
            ]);
    }

    private void WithSuccessfulCreate() =>
        _store
            .Setup(s => s.CreateJobAsync(It.IsAny<CreateJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Record());

    private CreateJobRequest Request(
        string name = "nightly review",
        string? projectPath = null,
        LLM llm = LLM.Claude,
        int? environmentId = 1,
        string prompt = "run the nightly review",
        int? timeoutMinutes = 60,
        List<JobTriggerRequest>? triggers = null) =>
        new(name, projectPath ?? _repoRoot, llm, environmentId, prompt, timeoutMinutes, true, triggers ?? []);

    private JobDefinitionRecord Record() => new(
        1, "nightly review", _repoRoot, LLM.Claude, 1, "nightly", "run the nightly review",
        60, true, DateTime.UtcNow, DateTime.UtcNow, null, []);

    private void RunGit(string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        process.WaitForExit();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); }
        catch { /* a leftover temp repo is not worth failing a test run over */ }
    }
}
