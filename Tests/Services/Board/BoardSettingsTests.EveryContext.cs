using Moq;
using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.Services.Board;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSettingsTests
{
    [Fact]
    public async Task AdditiveContextAndPinUpgradePreservesExistingContentAndOlderWriterFields()
    {
        var (board, lane, _, _) = await Lanes();
        var card = await Card(lane);
        await _boards.SaveContextSettingsAsync(_root, board, new("Existing Agent context", []), 0, Ct);
        // Disposable fixture representing the schema before these two additive migrations.
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE BoardContextSettings DROP COLUMN EveryContextMessage;
                ALTER TABLE BoardContextSettings DROP COLUMN EveryContextRevision;
                ALTER TABLE BoardCards DROP COLUMN Pinned;
                DELETE FROM SchemaMigrations WHERE Component IN ('board-every-context', 'board-card-pins');
                """;
            await command.ExecuteNonQueryAsync(Ct);
        }
        var upgraded = new BoardStore(_connectionString, _stateConnectionString);
        Assert.Equal(new BoardEveryContextSettingsRecord("", 0), await upgraded.GetEveryContextSettingsAsync(_root, board, Ct));
        Assert.Equal("Existing Agent context", (await upgraded.GetContextSettingsAsync(_root, board, Ct))!.Context.DefaultMessage);
        Assert.False((await upgraded.FindCardAsync(_root, card.Id, Ct))!.Pinned);
        await upgraded.SetCardPinnedAsync(_root, card.Id, true, Ct);
        await upgraded.SaveEveryContextSettingsAsync(_root, board, "Every launch", 0, Ct);
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE BoardCards SET Title = 'Older writer' WHERE Id = $id;";
            command.Parameters.AddWithValue("$id", card.Id);
            await command.ExecuteNonQueryAsync(Ct);
        }
        var reopened = new BoardStore(_connectionString, _stateConnectionString);
        Assert.True((await reopened.FindCardAsync(_root, card.Id, Ct))!.Pinned);
        Assert.Equal("Older writer", (await reopened.FindCardAsync(_root, card.Id, Ct))!.Title);
        Assert.Equal("Every launch", (await reopened.GetEveryContextSettingsAsync(_root, board, Ct))!.Message);
    }

    [Theory]
    [InlineData("base:codex", true, false, true)]
    [InlineData("base:codex", false, true, false)]
    [InlineData("env:42:codex", true, false, false)]
    [InlineData("env:42:codex", false, true, true)]
    public async Task ContextEstimateAndLaunchSampleIncludeEveryContextForTheSelectedLaunch(
        string selection, bool baseEnabled, bool customEnabled, bool expected)
    {
        var (board, lane, _, _) = await Lanes();
        var card = await Card(lane);
        await _boards.UpdateCardAsync(_root, card.Id, new(Assignee: selection), Ct);
        card = (await _boards.FindCardAsync(_root, card.Id, Ct))!;
        var service = new BoardService(_boards, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        await service.SaveContextSettingsAsync(_root, board, new(new("Board-specific context", []), 0), Ct);
        var settings = new Settings { CreateVibeStoryTracking = baseEnabled, CreateVibeStoryTrackingCustomEnvs = customEnabled };
        var estimator = new BoardContextEstimator(service, _boards, Mock.Of<IRepository>(), () => settings);
        var baseline = (await estimator.EstimateAsync(_root, card.Id, Ct))!;

        await service.SaveEveryContextSettingsAsync(_root, board, new("This board's message", 0), Ct);
        var estimate = (await estimator.EstimateAsync(_root, card.Id, Ct))!;
        var addedChars = expected ? EveryContextPrompt.Prefix("This board's message").Length : 0;
        Assert.Equal(baseline.Chars + addedChars, estimate.Chars);
        Assert.Equal(expected, estimate.Contents.Any(part => part.Key == "everyContext"));
        Assert.Contains(estimate.Contents, part => part.Key == "boardContext" && part.Chars > 0);
        var prompt = await BoardLaunchService.ComposePromptAsync(_boards, _root, card, "codex", null, "work", Ct);
        var sample = await estimator.RecordLaunchAsync(_root, card, prompt, "work", null, "codex", selection, Ct);
        Assert.Equal(estimate.Chars, sample!.Chars);
        Assert.Equal(estimate.Sources.Single(part => part.Key == "prompt").Tokens, sample.PromptTokens);
    }

    [Fact]
    public async Task EveryContextIsPerBoardScopedRevisionedAndIndependentOfOlderAgentContextWrites()
    {
        var (board, _, _, _) = await Lanes();
        var other = await _boards.CreateBoardAsync(_root, "Other", Ct);
        var service = new BoardService(_boards, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        Assert.Equal(new BoardEveryContextSettingsRecord("", 0), await service.GetEveryContextSettingsAsync(_root, board, Ct));
        var saved = await service.SaveEveryContextSettingsAsync(_root, board, new("  First board  ", 0), Ct);
        Assert.Equal(new BoardEveryContextSettingsRecord("First board", 1), saved);
        Assert.Equal("", (await service.GetEveryContextSettingsAsync(_root, other.Id, Ct))!.Message);
        Assert.Null(await service.SaveEveryContextSettingsAsync(_root + "-foreign", board, new("bad", 1), Ct));
        Assert.Null(await service.GetEveryContextSettingsAsync(_root + "-foreign", board, Ct));
        await Assert.ThrowsAsync<BoardConflictException>(() => service.SaveEveryContextSettingsAsync(_root, board, new("stale", 0), Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => service.SaveEveryContextSettingsAsync(_root, board, new(new('x', 4001), 1), Ct));
        // The older writer's unchanged SQL touches only ContextJson and Revision.
        await _boards.SaveContextSettingsAsync(_root, board, new("Agent context", []), 0, Ct);
        Assert.Equal(saved, await _boards.GetEveryContextSettingsAsync(_root, board, Ct));
        await service.SaveEveryContextSettingsAsync(_root, board, new("", 1), Ct);
        Assert.Equal("Agent context", (await _boards.GetContextSettingsAsync(_root, board, Ct))!.Context.DefaultMessage);
        var reopened = new BoardStore(_connectionString, _stateConnectionString);
        Assert.Equal(new BoardEveryContextSettingsRecord("", 2), await reopened.GetEveryContextSettingsAsync(_root, board, Ct));
        await service.SaveEveryContextSettingsAsync(_root, other.Id, new("Other board", 0), Ct);
        Assert.Equal("Other board", await EveryContextPrompt.ResolveMessageAsync(_boards, _root, other.Id, Ct));
        Assert.Equal("", await EveryContextPrompt.ResolveMessageAsync(_boards, _root, board, Ct));
        Assert.Null(await EveryContextPrompt.ResolveMessageAsync(_boards, _root + "-foreign", other.Id, Ct));
    }
}
