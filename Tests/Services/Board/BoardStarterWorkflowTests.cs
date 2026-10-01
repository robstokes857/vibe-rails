using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardStarterWorkflowTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-starter-" + Guid.NewGuid().ToString("N"));
    private readonly string state, board;
    private readonly Repository repository;
    private readonly BoardStore boards;
    private readonly JobStore jobs;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardStarterWorkflowTests()
    {
        Directory.CreateDirectory(root);
        state = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
        board = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        repository = new(state);
        boards = new(board, state);
        jobs = new(state, boards);
    }

    private BoardStarterWorkflowService Recovery(IBoardStore? store = null, IJobStore? jobStore = null) =>
        new(store ?? boards, jobStore ?? jobs, NullLogger<BoardStarterWorkflowService>.Instance);
    private BoardService Service(BoardStarterWorkflowService? recovery = null) =>
        new(boards, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe(), recovery ?? Recovery());

    [Fact]
    public async Task RootSchedulerFinishesAFirstBoardCreatedByTheLeanMcpHostWithoutLaunching()
    {
        // The stdio host never runs the full state migration or root scheduler.
        var lean = new BoardService(boards, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        await lean.GetColumnsAsync(root, Ct);
        Assert.Single(await boards.GetPendingStarterWorkflowsAsync(root, Ct));
        var resolver = new Mock<IBoardProjectResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(root);
        await using var services = new ServiceCollection().AddSingleton(Recovery())
            .AddSingleton(Mock.Of<IJobLaunchService>()).BuildServiceProvider();
        var scheduler = new JobSchedulerHostedService(services.GetRequiredService<IServiceScopeFactory>(), jobs,
            projectResolver: resolver.Object);
        await scheduler.RunCycleAsync(DateTime.UtcNow, Ct);
        Assert.Empty(await boards.GetPendingStarterWorkflowsAsync(root, Ct));
        Assert.Single(await jobs.GetJobsAsync(root, cancellationToken: Ct));
        Assert.Empty(await jobs.GetRunsAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task FirstAndAdditionalBoardsUseOneRecipeAndNeverLaunchOnCreationOrSave()
    {
        var service = Service();
        await service.GetBoardsAsync(root, Ct);
        await service.CreateBoardAsync(root, new("Another"), Ct);
        await service.GetBoardsAsync(root, Ct);
        Assert.Equal(2, (await boards.GetBoardsAsync(root, Ct)).Count);
        var catalog = await jobs.GetJobsAsync(root, cancellationToken: Ct);
        Assert.Equal(2, catalog.Count);
        foreach (var job in catalog)
        {
            Assert.Equal(new[] { JobActionKind.CodeQuality, JobActionKind.Vca, JobActionKind.Worker }, job.Actions!.Select(a => a.Kind));
            Assert.All(job.Actions!.Take(2), action => Assert.Equal(new[] { "unpushed" }, action.Arguments));
            var worker = (await repository.GetEnvironmentByIdAsync(job.EnvironmentId!.Value, Ct))!;
            Assert.Equal("code_review", worker.Purpose);
            Assert.True(worker.AutomationWorker);
            Assert.Equal("base:codex", worker.ReviewerRouting!.Mappings[0].Reviewer.Selection);
            Assert.Equal("base:claude", worker.ReviewerRouting.Mappings[1].Reviewer.Selection);
            Assert.Contains("get_board_reviews", worker.CustomPrompt);
            Assert.Contains("read_board_check", worker.CustomPrompt);
        }
        foreach (var b in await boards.GetBoardsAsync(root, Ct))
        {
            var columns = await boards.GetColumnsAsync(root, Ct, b.Id);
            Assert.Equal(BoardStore.DefaultLanes.Select(l => l.Name), columns.Select(c => c.Name));
            var setting = (await boards.GetLaneAutomationAsync(root, columns[3].Id, Ct))!;
            Assert.Single(setting.JobIds);
            await boards.SaveLaneAutomationAsync(root, columns[3].Id, setting.JobIds, setting.Revision, Ct);
        }
        Assert.Empty(await jobs.GetRunsAsync(cancellationToken: Ct));
        Assert.Empty(await boards.GetDueLaneAutomationsAsync(DateTime.UtcNow.AddMinutes(5), Ct));
    }

    [Fact]
    public async Task CrashBetweenCommitsAndConcurrentRecoveryReuseTheWorkerJobAndAssignment()
    {
        await boards.EnsureDefaultColumnsAsync(root, Ct);
        var pending = Assert.Single(await boards.GetPendingStarterWorkflowsAsync(root, Ct));
        var id = await jobs.EnsureBoardReviewRecipeAsync(root, pending.ColumnId, pending.RecipeId, Ct);
        // Model process loss after state commit and before Board acknowledgement.
        var reopened = new BoardStore(board, state);
        await Task.WhenAll(Task.Run(() => Recovery(reopened, new JobStore(state)).RecoverAsync(root, Ct), Ct),
            Task.Run(() => Recovery().RecoverAsync(root, Ct), Ct));
        await Recovery().RecoverAsync(root, Ct);
        Assert.Equal(id, Assert.Single((await boards.GetLaneAutomationAsync(root, pending.ColumnId, Ct))!.JobIds));
        Assert.Single(await jobs.GetJobsAsync(root, cancellationToken: Ct));
        Assert.Single(await repository.GetAllEnvironmentsAsync(Ct), e => e.AutomationWorker);
        Assert.Equal(1, (await boards.GetLaneAutomationAsync(root, pending.ColumnId, Ct))!.Revision);
    }

    [Fact]
    public async Task FailedStateTransactionRollsBackWorkerAndRecoversWithoutAnotherBoard()
    {
        await Sql(state, "CREATE TRIGGER FailStarter BEFORE INSERT ON Jobs BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");
        var created = await Service().CreateBoardAsync(root, new("Keep me"), Ct);
        Assert.Single(await boards.GetBoardsAsync(root, Ct));
        Assert.Empty(await repository.GetAllEnvironmentsAsync(Ct));
        var seed = Assert.Single(await boards.GetPendingStarterWorkflowsAsync(root, Ct));
        Assert.True((await new BoardAutomationService(boards, jobs, Recovery()).GetAsync(root, seed.ColumnId, Ct))!.StarterSetupPending);
        await Sql(state, "DROP TRIGGER FailStarter;");
        await Service().GetBoardsAsync(root, Ct);
        Assert.Equal(created.Id, Assert.Single(await boards.GetBoardsAsync(root, Ct)).Id);
        Assert.Single(await jobs.GetJobsAsync(root, cancellationToken: Ct));
        Assert.Empty(await boards.GetPendingStarterWorkflowsAsync(root, Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserRemovalBeforeOrAfterAssignmentSurvivesReopen(bool alreadyAssigned)
    {
        await boards.EnsureDefaultColumnsAsync(root, Ct);
        var seed = Assert.Single(await boards.GetPendingStarterWorkflowsAsync(root, Ct));
        var jobId = await jobs.EnsureBoardReviewRecipeAsync(root, seed.ColumnId, seed.RecipeId, Ct);
        if (alreadyAssigned) await boards.CompleteStarterWorkflowAsync(root, seed.ColumnId, jobId, Ct);
        var settings = (await boards.GetLaneAutomationAsync(root, seed.ColumnId, Ct))!;
        await boards.SaveLaneAutomationAsync(root, seed.ColumnId, [], settings.Revision, Ct);
        // A stale recovery that already read the intent cannot undo the user's save.
        await boards.CompleteStarterWorkflowAsync(root, seed.ColumnId, jobId, Ct);
        var reopened = new BoardStore(board, state);
        await Recovery(reopened).RecoverAsync(root, Ct);
        Assert.Empty((await reopened.GetLaneAutomationAsync(root, seed.ColumnId, Ct))!.JobIds);
        Assert.Empty(await reopened.GetPendingStarterWorkflowsAsync(root, Ct));
        Assert.NotNull(await jobs.GetJobAsync(jobId, Ct));
    }

    [Fact]
    public async Task RenamesDuplicateNamesAndFirstPositionKeepTheCapturedLaneIdentity()
    {
        await boards.EnsureDefaultColumnsAsync(root, Ct);
        var seed = Assert.Single(await boards.GetPendingStarterWorkflowsAsync(root, Ct));
        var columns = await boards.GetColumnsAsync(root, Ct);
        await boards.UpdateColumnAsync(root, seed.ColumnId, "Ship when ready", null, Ct);
        await boards.UpdateColumnAsync(root, columns[0].Id, "Ship when ready", null, Ct);
        await boards.ReorderColumnsAsync(root, new[] { seed.ColumnId }.Concat(columns.Where(c => c.Id != seed.ColumnId).Select(c => c.Id)).ToList(), Ct);
        await Recovery().RecoverAsync(root, Ct);
        Assert.Equal(seed.ColumnId, (await boards.GetColumnsAsync(root, Ct))[0].Id);
        Assert.Single((await boards.GetLaneAutomationAsync(root, seed.ColumnId, Ct))!.JobIds);
        Assert.Empty((await boards.GetLaneAutomationAsync(root, columns[0].Id, Ct))!.JobIds);
    }

    [Fact]
    public async Task CreationSkippedAndBackwardEntriesKeepTheSixtySecondDelay()
    {
        await Service().GetBoardsAsync(root, Ct);
        var columns = await boards.GetColumnsAsync(root, Ct);
        var review = columns[3].Id;
        var created = await boards.CreateCardAsync(root, new(review, "Created here", "", null, "medium", null, [], false), Ct);
        var due = Assert.Single(await boards.GetPendingLaneAutomationsAsync(root, created.Id, Ct)).DueUtc;
        Assert.InRange((due - DateTime.UtcNow).TotalSeconds, 55, 61);
        foreach (var source in new[] { columns[0].Id, columns[4].Id })
        {
            await boards.MoveCardAsync(root, created.Id, source, null, Ct);
            Assert.Empty(await boards.GetPendingLaneAutomationsAsync(root, created.Id, Ct));
            await boards.MoveCardAsync(root, created.Id, review, null, Ct);
            Assert.Single(await boards.GetPendingLaneAutomationsAsync(root, created.Id, Ct));
        }
        var setting = (await boards.GetLaneAutomationAsync(root, review, Ct))!;
        await boards.SaveLaneAutomationAsync(root, review, setting.JobIds, setting.Revision, Ct);
        Assert.Empty(await boards.GetPendingLaneAutomationsAsync(root, created.Id, Ct));
    }

    [Fact]
    public async Task UpgradeDoesNotRetrofitAnExistingBoardOrChangeItsSettingsAndHistory()
    {
        await boards.EnsureDefaultColumnsAsync(root, Ct);
        var columns = await boards.GetColumnsAsync(root, Ct);
        var card = await boards.CreateCardAsync(root, new(columns[3].Id, "Existing", "Keep history", null, "medium", null, [], false), Ct);
        await boards.SaveLaneAutomationAsync(root, columns[3].Id, [], 0, Ct);
        var history = await boards.GetHistoryAsync(root, columns[0].BoardId, null, 0, Ct);
        // Reconstruct the prior release's schema in this disposable fixture.
        await Sql(board, "DROP TRIGGER BoardStarterWorkflows_SettingsInsert; DROP TRIGGER BoardStarterWorkflows_SettingsUpdate; DROP TABLE BoardStarterWorkflows; DELETE FROM SchemaMigrations WHERE Component = 'board-starter-workflows';");
        var upgraded = new BoardStore(board, state);
        await Recovery(upgraded).RecoverAsync(root, Ct);
        Assert.Empty(await upgraded.GetPendingStarterWorkflowsAsync(root, Ct));
        Assert.Empty(await jobs.GetJobsAsync(root, cancellationToken: Ct));
        Assert.Equal(1, (await upgraded.GetLaneAutomationAsync(root, columns[3].Id, Ct))!.Revision);
        Assert.Equal(history, await upgraded.GetHistoryAsync(root, columns[0].BoardId, null, 0, Ct));
        Assert.Equal("Keep history", (await upgraded.FindCardAsync(root, card.Id, Ct))!.Description);
    }

    [Fact]
    public async Task MissingProviderIsVisibleWithoutChangingTheRoutingOrDisablingTheDefault()
    {
        await Service().GetBoardsAsync(root, Ct);
        var lane = (await boards.GetColumnsAsync(root, Ct))[3];
        var executable = new Mock<IJobExecutableResolver>();
        executable.Setup(e => e.Resolve(LLM.Codex)).Returns("codex");
        var service = new BoardAutomationService(boards, jobs, Recovery(), repository, executable.Object);
        var setting = (await service.GetAsync(root, lane.Id, Ct))!;
        var choice = Assert.Single(setting.Jobs);
        Assert.True(choice.Enabled);
        Assert.Contains("Install/sign in to claude", choice.Setup);
        Assert.Contains("No provider will be substituted", choice.Setup);
        Assert.Single(setting.JobIds);
        Assert.Empty(await jobs.GetRunsAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task DeletedAutomationAndDeletedLaneAreNeverRecreated()
    {
        await boards.EnsureDefaultColumnsAsync(root, Ct);
        var seed = Assert.Single(await boards.GetPendingStarterWorkflowsAsync(root, Ct));
        var job = await jobs.EnsureBoardReviewRecipeAsync(root, seed.ColumnId, seed.RecipeId, Ct);
        await jobs.SoftDeleteJobAsync(job, Ct);
        await Recovery().RecoverAsync(root, Ct);
        Assert.Empty((await boards.GetLaneAutomationAsync(root, seed.ColumnId, Ct))!.JobIds);
        Assert.Empty(await boards.GetPendingStarterWorkflowsAsync(root, Ct));
        Assert.Single(await jobs.GetJobsAsync(root, includeDeleted: true, cancellationToken: Ct));
        await boards.DeleteColumnAsync(root, seed.ColumnId, Ct);
        await Recovery().RecoverAsync(root, Ct);
        Assert.Equal(4, (await boards.GetColumnsAsync(root, Ct)).Count);
    }

    [Fact]
    public async Task RecoveryPreservesWorkerEditsAndCannotAssignAcrossProjects()
    {
        await boards.EnsureDefaultColumnsAsync(root, Ct);
        var seed = Assert.Single(await boards.GetPendingStarterWorkflowsAsync(root, Ct));
        var id = await jobs.EnsureBoardReviewRecipeAsync(root, seed.ColumnId, seed.RecipeId, Ct);
        var job = (await jobs.GetJobAsync(id, Ct))!;
        var worker = (await repository.GetEnvironmentByIdAsync(job.EnvironmentId!.Value, Ct))!;
        worker.CustomName = "My review";
        worker.CustomPrompt = "My instructions";
        worker.ReviewerRouting = new("switch", [], new("base:claude"));
        await repository.UpdateEnvironmentAsync(worker, Ct);
        Assert.Equal(id, await jobs.EnsureBoardReviewRecipeAsync(root, seed.ColumnId, seed.RecipeId, Ct));
        var retained = (await repository.GetEnvironmentByIdAsync(worker.Id, Ct))!;
        Assert.Equal("My instructions", retained.CustomPrompt);
        Assert.Equal("base:claude", retained.ReviewerRouting!.Fallback.Selection);
        await boards.CompleteStarterWorkflowAsync(root + "-foreign", seed.ColumnId, id, Ct);
        Assert.Single(await boards.GetPendingStarterWorkflowsAsync(root, Ct));
        await jobs.UpdateJobAsync(id, new("Moved", root + "-foreign", job.Llm, job.EnvironmentId,
            job.Prompt, job.TimeoutMinutes, true, []), Ct);
        await Recovery().RecoverAsync(root, Ct);
        Assert.Empty((await boards.GetLaneAutomationAsync(root, seed.ColumnId, Ct))!.JobIds);
        Assert.Empty(await boards.GetPendingStarterWorkflowsAsync(root, Ct));
        Assert.Single(await jobs.GetJobsAsync(root + "-foreign", cancellationToken: Ct));
    }

    private async Task Sql(string connectionString, string sql)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    public void Dispose() => Directory.Delete(root, true);
}
