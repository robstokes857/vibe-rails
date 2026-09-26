using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardCardAutomationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"board-card-runs-{Guid.NewGuid():N}");
    private readonly BoardStore _boards;
    private readonly JobStore _jobs;
    private readonly BoardCardAutomationService _service;
    private readonly Mock<IJobScheduler> _scheduler = new();
    private readonly Mock<IAutomationScriptService> _scripts = new();
    private string Project => Path.Combine(_root, "project");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardCardAutomationTests()
    {
        Directory.CreateDirectory(Project);
        var state = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "state.db"), Pooling = false }.ToString();
        var board = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_root, "board.db"), Pooling = false }.ToString();
        StateDatabaseSchema.Ensure(state);
        _boards = new BoardStore(board, state);
        _jobs = new JobStore(state);
        var runner = new JobService(_jobs, new Mock<IRepository>().Object,
            new Mock<IJobExecutableResolver>().Object, _scheduler.Object, _scripts.Object);
        _service = new BoardCardAutomationService(_boards, _jobs, runner);
    }

    private async Task<BoardCardRecord> Card(string? project = null)
    {
        await _boards.EnsureDefaultColumnsAsync(project ?? Project, Ct);
        return await _boards.CreateCardAsync(project ?? Project, new(null, "Card", "", null, "medium", null, [], false), Ct);
    }

    private Task<JobDefinitionRecord> Job(string? project = null, bool enabled = true) =>
        _jobs.CreateJobAsync(new("Review code", project ?? Project, LLM.NotSet, null, "", null, enabled, [],
            Actions: [new JobActionRequest("script", JobActionKind.Script, ScriptPath: "review.py",
                ScriptRuntime: JobScriptRuntime.Python, Arguments: ["--check"], ApprovedHash: new string('a', 64))]), Ct);

    [Fact]
    public async Task RunSnapshotsActionsAndCard_ThenLinksItsRecordingWithoutChangingTheCard()
    {
        var card = await Card();
        var job = await Job();
        var queued = await _service.RunAsync(Project, card.Id, job.Id, Ct);
        var run = (await _jobs.GetRunAsync(queued!.RunId!, Ct))!;
        Assert.Equal(JobTriggerKind.Manual, run.TriggerKind);
        Assert.Equal(card.Key, JobRunner.GetBoardCardKey(run));
        Assert.True(run.LaunchInTerminalTab);
        Assert.Equal("review.py", Assert.Single(run.Actions!).ScriptPath);
        _scheduler.Verify(s => s.Kick(), Times.Once);
        var response = (await _service.GetAsync(Project, card.Id, Ct))!;
        Assert.Equal(run.Id, Assert.Single(response.Runs).Id);
        Assert.Equal(JobRunStatus.Queued, response.Runs[0].Status);

        await _jobs.LinkRunTerminalSessionAsync(run.Id, "recording", Ct);
        await BoardAutomationSessionLinker.LinkAsync(_boards, run, "recording", "tab", Ct);
        await BoardAutomationSessionLinker.LinkAsync(_boards, run, "recording", "tab", Ct);
        var detail = (await _boards.GetCardDetailAsync(Project, card.Id, Ct))!;
        var recording = Assert.Single(detail.Sessions);
        Assert.Equal("recording", recording.SessionId);
        Assert.Equal(BoardSessionRecord.AutomationOrigin, recording.Origin);
        Assert.Equal(card.ColumnId, detail.Card.ColumnId);
        Assert.Equal(card.Description, detail.Card.Description);
        Assert.Empty((await _service.GetAsync(Project, card.Id, Ct))!.Runs);
    }

    [Fact]
    public async Task ForeignCardsAndAutomationsAreRejected_AndCatalogIsScoped()
    {
        var card = await Card();
        var other = Path.Combine(_root, "other");
        var foreignCard = await Card(other);
        var job = await Job();
        var foreignJob = await Job(other);
        Assert.Equal(job.Id, Assert.Single((await _service.GetAsync(Project, card.Id, Ct))!.Jobs).Id);
        Assert.Null(await _service.GetAsync(Project, foreignCard.Id, Ct));
        Assert.Null(await _service.RunAsync(Project, foreignCard.Id, job.Id, Ct));
        var error = await Assert.ThrowsAsync<JobServiceException>(() => _service.RunAsync(Project, card.Id, foreignJob.Id, Ct));
        Assert.Equal(404, error.StatusCode);
        Assert.Null(await _jobs.EnqueueBoardCardRunAsync(Project, foreignJob.Id, card.Key, Ct));
        Assert.Empty(await _jobs.GetRunsAsync(cancellationToken: Ct));
        _scheduler.Verify(s => s.Kick(), Times.Never);
    }

    [Fact]
    public async Task DisabledUnavailableAndOverlappingRunsAreRejected()
    {
        var card = await Card();
        var disabled = await Job(enabled: false);
        Assert.Equal(400, (await Assert.ThrowsAsync<JobServiceException>(() => _service.RunAsync(Project, card.Id, disabled.Id, Ct))).StatusCode);
        Assert.Null(await _jobs.EnqueueBoardCardRunAsync(Project, disabled.Id, card.Key, Ct));
        var job = await Job();
        _scripts.Setup(s => s.GetRuntimeUnavailableMessage(JobScriptRuntime.Python)).Returns("Python is unavailable.");
        Assert.Equal(400, (await Assert.ThrowsAsync<JobServiceException>(() => _service.RunAsync(Project, card.Id, job.Id, Ct))).StatusCode);
        Assert.Empty(await _jobs.GetRunsAsync(cancellationToken: Ct));
        _scripts.Setup(s => s.GetRuntimeUnavailableMessage(JobScriptRuntime.Python)).Returns((string?)null);
        await _service.RunAsync(Project, card.Id, job.Id, Ct);
        var secondCard = await Card();
        Assert.Equal(409, (await Assert.ThrowsAsync<JobServiceException>(() => _service.RunAsync(Project, secondCard.Id, job.Id, Ct))).StatusCode);
        Assert.Empty((await _service.GetAsync(Project, secondCard.Id, Ct))!.Runs);
        Assert.Single(await _jobs.GetRunsAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task FailedLaunchRemainsLinkedAfterReopen_OrdinaryRetryDoesNotInheritCardContext()
    {
        var card = await Card();
        var job = await Job();
        var queued = (await _service.RunAsync(Project, card.Id, job.Id, Ct))!;
        await _jobs.CompleteRunAsync(queued.RunId!, JobRunStatus.Failed, 1, "Terminal could not start.", Ct);
        var failed = Assert.Single((await _service.GetAsync(Project, card.Key, Ct))!.Runs);
        Assert.Equal(JobRunStatus.Failed, failed.Status);
        Assert.Equal("Terminal could not start.", failed.ErrorMessage);
        Assert.Empty(await _jobs.GetBoardCardRunsAsync(Path.Combine(_root, "other"), card.Key, Ct));
        Assert.Empty(await _jobs.GetBoardCardRunsAsync(Project, card.Key + "0", Ct));
        var retry = (await _jobs.GetRunAsync((await _jobs.EnqueueRetryAsync(queued.RunId!, Ct))!, Ct))!;
        Assert.Null(JobRunner.GetBoardCardKey(retry));
        Assert.False(retry.LaunchInTerminalTab);
        Assert.Single((await _service.GetAsync(Project, card.Key, Ct))!.Runs);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(25)]
    public async Task RecentUnlinkedRunsAreLimitedAfterExcludingNewerRecordings(int unlinkedCount)
    {
        var card = await Card();
        var unlinkedJob = await Job();
        var recordedJob = await Job();
        var unlinked = new List<string>();
        for (var i = 0; i < unlinkedCount; i++)
        {
            var run = (await _service.RunAsync(Project, card.Id, unlinkedJob.Id, Ct))!;
            unlinked.Add(run.RunId!);
            // Leave the final run queued, and the earlier ones failed before recording.
            if (i < unlinkedCount - 1)
                await _jobs.CompleteRunAsync(run.RunId!, JobRunStatus.Failed, 1, "Could not start", Ct);
        }
        for (var i = 0; i < 25; i++)
        {
            var queued = (await _service.RunAsync(Project, card.Id, recordedJob.Id, Ct))!;
            var run = (await _jobs.GetRunAsync(queued.RunId!, Ct))!;
            await _jobs.LinkRunTerminalSessionAsync(run.Id, $"recording-{i}", Ct);
            await BoardAutomationSessionLinker.LinkAsync(_boards, run, $"recording-{i}", null, Ct);
            await _jobs.CompleteRunAsync(run.Id, JobRunStatus.Succeeded, 0, null, Ct);
        }

        var visible = (await _service.GetAsync(Project, card.Key, Ct))!.Runs;
        Assert.Equal(unlinked.AsEnumerable().Reverse().Take(20), visible.Select(run => run.Id));
        Assert.Equal(JobRunStatus.Queued, visible[0].Status);
        Assert.All(visible.Skip(1), run => Assert.Equal(JobRunStatus.Failed, run.Status));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
