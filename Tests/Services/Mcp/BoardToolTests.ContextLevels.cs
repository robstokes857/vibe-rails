using Microsoft.Data.Sqlite;
using VibeRails.Services.Board;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Mcp;

/// <summary>
/// VIBE-229 review N2: get_board_card shows App and Board context under the Nudge switch the
/// caller's own launch used. A session attached through MCP has an empty link selection, so the
/// launch's recorded environment decides, not the base switch.
/// </summary>
public sealed partial class BoardToolTests
{
    [Theory]
    [InlineData("Review worker", false)]
    [InlineData(null, true)]
    public async Task GetBoardCard_AttachedSessionWithoutSelection_UsesItsRecordedLaunchForTheNudge(string? environment, bool appAndBoard)
    {
        await _tool.CreateBoardCard("A", cancellationToken: Ct);
        var boardId = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!.BoardId!;
        await _service.SaveAppContextSettingsAsync(new("APP level", 0), Ct);
        await _service.SaveEveryContextSettingsAsync(_project, boardId, new("BOARD level", 0), Ct);
        await _service.SaveContextSettingsAsync(_project, boardId, new(new("CARD level", []), 0), Ct);
        _resolver.CurrentSessionId = "22222222-2222-4222-8222-222222222222";
        await using (var connection = new SqliteConnection(_stateConnectionString))
        {
            await connection.OpenAsync(Ct);
            await using var insert = connection.CreateCommand();
            insert.CommandText = """
                CREATE TABLE Sessions (Id TEXT PRIMARY KEY, Cli TEXT, EnvironmentName TEXT);
                INSERT INTO Sessions VALUES ($id, 'claude', $environment);
                """;
            insert.Parameters.AddWithValue("$id", _resolver.CurrentSessionId);
            insert.Parameters.AddWithValue("$environment", (object?)environment ?? DBNull.Value);
            await insert.ExecuteNonQueryAsync(Ct);
        }
        Assert.Equal("", (await _service.AttachSessionAsync(_project, "PROJ-1", _resolver.CurrentSessionId, null, Ct))!.Selection);
        // Base launches nudged, saved environments not: the two switches disagree on purpose.
        var tool = new BoardTool(_service, _resolver, _store, new BoardWorkflowService(_store, new BoardReviewService(_store, _service)),
            launchSettings: () => new Settings { CreateVibeStoryTracking = true, CreateVibeStoryTrackingCustomEnvs = false });

        var read = await tool.GetBoardCard("PROJ-1", cancellationToken: Ct);

        Assert.Contains("Card context:\nCARD level", read);
        Assert.Equal(appAndBoard, read.Contains("App context:\nAPP level", StringComparison.Ordinal));
        Assert.Equal(appAndBoard, read.Contains("Board context:\nBOARD level", StringComparison.Ordinal));
    }
}
