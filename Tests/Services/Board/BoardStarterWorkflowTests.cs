using Microsoft.Data.Sqlite;
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

    private BoardService Service(IBoardStore? store = null) =>
        new(store ?? boards, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());

    [Fact]
    public async Task FirstAndAdditionalBoardsHaveLanesWithoutCreatingReviewAgentsOrRecipes()
    {
        var service = Service();
        await service.GetBoardsAsync(root, Ct);
        await service.CreateBoardAsync(root, new("Another"), Ct);
        await service.GetBoardsAsync(root, Ct);
        Assert.Equal(2, (await boards.GetBoardsAsync(root, Ct)).Count);
        Assert.Empty(await boards.GetPendingStarterWorkflowsAsync(root, Ct));
        Assert.Empty(await jobs.GetJobsAsync(root, cancellationToken: Ct));
        Assert.Empty(await repository.GetAllEnvironmentsAsync(Ct));
        foreach (var b in await boards.GetBoardsAsync(root, Ct))
        {
            var columns = await boards.GetColumnsAsync(root, Ct, b.Id);
            Assert.Equal(BoardStore.DefaultLanes.Select(l => l.Name), columns.Select(c => c.Name));
            foreach (var column in columns)
            {
                var setting = (await new BoardAutomationService(boards, jobs).GetAsync(root, column.Id, Ct))!;
                Assert.Empty(setting.JobIds);
                Assert.Empty(setting.Jobs);
                Assert.False(setting.StarterSetupPending);
            }
            var card = await boards.CreateCardAsync(root, new(columns[3].Id, "Work", "Keep this card", null, "medium", null, [], false), Ct);
            Assert.Empty(await boards.GetPendingLaneAutomationsAsync(root, card.Id, Ct));
        }
        Assert.Empty(await jobs.GetRunsAsync(cancellationToken: Ct));
    }

    [Fact]
    public async Task OldPendingIntentSurvivesBoardAccessAndSchedulerWithoutInstallingAReviewer()
    {
        var lane = await AddLegacyIntentAsync();
        await Service().GetBoardsAsync(root, Ct);
        var resolver = new Mock<IBoardProjectResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(root);
        await using var services = new ServiceCollection().AddSingleton(Mock.Of<IJobLaunchService>()).BuildServiceProvider();
        var scheduler = new JobSchedulerHostedService(services.GetRequiredService<IServiceScopeFactory>(), jobs,
            projectResolver: resolver.Object);
        await scheduler.RunCycleAsync(DateTime.UtcNow, Ct);
        var setting = (await new BoardAutomationService(boards, jobs).GetAsync(root, lane, Ct))!;
        Assert.False(setting.StarterSetupPending);
        Assert.Empty(setting.Jobs);
        Assert.Empty(setting.JobIds);
        Assert.Equal(lane, Assert.Single(await boards.GetPendingStarterWorkflowsAsync(root, Ct)).ColumnId);
        Assert.Empty(await jobs.GetJobsAsync(root, cancellationToken: Ct));
        Assert.Empty(await repository.GetAllEnvironmentsAsync(Ct));
        Assert.Empty(await jobs.GetRunsAsync(cancellationToken: Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreviouslyInstalledReviewerAndSelectionRemainUnchanged(bool selected)
    {
        var lane = await AddLegacyIntentAsync();
        // Model an earlier release's installation. New Board creation never calls this compatibility method.
        var jobId = await jobs.EnsureBoardReviewRecipeAsync(root, lane, BoardReviewDefaults.RecipeId, Ct);
        await boards.CompleteStarterWorkflowAsync(root, lane, selected ? jobId : null, Ct);
        var job = (await jobs.GetJobAsync(jobId, Ct))!;
        var worker = (await repository.GetEnvironmentByIdAsync(job.EnvironmentId!.Value, Ct))!;
        worker.CustomName = "My reviewer";
        worker.CustomPrompt = "Keep my review instructions";
        worker.ReviewerRouting = new("switch", [], new("base:claude"));
        await repository.UpdateEnvironmentAsync(worker, Ct);
        var card = await boards.CreateCardAsync(root, new(null, "Existing", "Keep card contents", null, "medium", null, [], false), Ct);
        var before = (await boards.GetLaneAutomationAsync(root, lane, Ct))!;
        var history = await boards.GetHistoryAsync(root, (await boards.GetColumnsAsync(root, Ct))[0].BoardId, null, 0, Ct);

        var reopened = new BoardStore(board, state);
        await Service(reopened).GetBoardsAsync(root, Ct);
        await Service(reopened).CreateBoardAsync(root, new("Later board"), Ct);
        var setting = (await new BoardAutomationService(reopened, jobs).GetAsync(root, lane, Ct))!;
        Assert.Equal(before.JobIds, setting.JobIds);
        Assert.Equal(before.Revision, setting.Revision);
        Assert.False(setting.StarterSetupPending);
        Assert.Equal(jobId, Assert.Single(setting.Jobs).Id);
        Assert.Equal(jobId, Assert.Single(await jobs.GetJobsAsync(root, cancellationToken: Ct)).Id);
        var retained = (await repository.GetEnvironmentByIdAsync(worker.Id, Ct))!;
        Assert.Equal(worker.CustomName, retained.CustomName);
        Assert.Equal(worker.CustomPrompt, retained.CustomPrompt);
        Assert.Equivalent(worker.ReviewerRouting, retained.ReviewerRouting);
        Assert.Equal("Keep card contents", (await reopened.FindCardAsync(root, card.Id, Ct))!.Description);
        Assert.Equal(history, await reopened.GetHistoryAsync(root, (await boards.GetColumnsAsync(root, Ct))[0].BoardId, null, 0, Ct));
        Assert.Empty(await jobs.GetRunsAsync(cancellationToken: Ct));
    }

    private async Task<string> AddLegacyIntentAsync()
    {
        await boards.EnsureDefaultColumnsAsync(root, Ct);
        var lane = (await boards.GetColumnsAsync(root, Ct))[3].Id;
        await using var db = new SqliteConnection(board);
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO BoardStarterWorkflows (ColumnId, RecipeId) VALUES ($column, $recipe);";
        command.Parameters.AddWithValue("$column", lane);
        command.Parameters.AddWithValue("$recipe", BoardReviewDefaults.RecipeId);
        await command.ExecuteNonQueryAsync(Ct);
        return lane;
    }

    public void Dispose() => Directory.Delete(root, true);
}
