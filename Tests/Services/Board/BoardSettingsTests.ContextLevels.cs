using Moq;
using Microsoft.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// VIBE-229: App, Board, Card and Card type context are one system of scopes. These tests cover
/// the storage additions, the order and gating of the levels, and that the launch, the context
/// estimate and get_board_card show the same block.
/// </summary>
public sealed partial class BoardSettingsTests
{
    private const string Heading = "Context supplied by the user, broadest first:";

    [Fact]
    public async Task AdditiveContextAndPinUpgradePreservesExistingContentAndOlderWriterFields()
    {
        var (board, lane, _, _) = await Lanes();
        var card = await Card(lane);
        await _boards.SaveContextSettingsAsync(_root, board, new("Existing Agent context", []), 0, Ct);
        // Disposable fixture representing the schema before these additive migrations.
        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(Ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE BoardContextSettings DROP COLUMN EveryContextMessage;
                ALTER TABLE BoardContextSettings DROP COLUMN EveryContextRevision;
                ALTER TABLE BoardCards DROP COLUMN Pinned;
                DROP TABLE BoardAppContextSettings;
                DELETE FROM SchemaMigrations WHERE Component IN ('board-every-context', 'board-card-pins', 'board-app-context');
                """;
            await command.ExecuteNonQueryAsync(Ct);
        }
        var upgraded = new BoardStore(_connectionString, _stateConnectionString);
        Assert.Equal(new BoardEveryContextSettingsRecord("", 0), await upgraded.GetEveryContextSettingsAsync(_root, board, Ct));
        Assert.Equal(new AppContextSettingsRecord("", 0), await upgraded.GetAppContextSettingsAsync(Ct));
        Assert.Equal("Existing Agent context", (await upgraded.GetContextSettingsAsync(_root, board, Ct))!.Context.DefaultMessage);
        Assert.False((await upgraded.FindCardAsync(_root, card.Id, Ct))!.Pinned);
        await upgraded.SetCardPinnedAsync(_root, card.Id, true, Ct);
        await upgraded.SaveEveryContextSettingsAsync(_root, board, "Every launch", 0, Ct);
        await upgraded.SaveAppContextSettingsAsync("App launch", 0, Ct);
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
        Assert.Equal(new AppContextSettingsRecord("App launch", 1), await reopened.GetAppContextSettingsAsync(Ct));
    }

    [Theory]
    [InlineData("base:codex", true, false, true)]
    [InlineData("base:codex", false, true, false)]
    [InlineData("env:42:codex", true, false, false)]
    [InlineData("env:42:codex", false, true, true)]
    public async Task ContextEstimateAndLaunchSampleIncludeAppAndBoardContextOnlyForTheSelectedLaunchNudge(
        string selection, bool baseEnabled, bool customEnabled, bool expected)
    {
        var (board, lane, _, _) = await Lanes();
        var card = await Card(lane);
        await _boards.UpdateCardAsync(_root, card.Id, new(Assignee: selection), Ct);
        card = (await _boards.FindCardAsync(_root, card.Id, Ct))!;
        var service = new BoardService(_boards, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        await service.SaveContextSettingsAsync(_root, board, new(new("Card-level context", []), 0), Ct);
        var settings = new Settings { CreateVibeStoryTracking = baseEnabled, CreateVibeStoryTrackingCustomEnvs = customEnabled };
        var estimator = new BoardContextEstimator(service, _boards, Mock.Of<IRepository>(), () => settings);
        var baseline = (await estimator.EstimateAsync(_root, card.Id, Ct))!;

        await service.SaveEveryContextSettingsAsync(_root, board, new("This board's message", 0), Ct);
        await service.SaveAppContextSettingsAsync(new("Every app launch", 0), Ct);
        var estimate = (await estimator.EstimateAsync(_root, card.Id, Ct))!;
        var levels = estimate.Contents.Where(part => part.Key.EndsWith("Context", StringComparison.Ordinal)).Select(part => part.Key).ToList();
        Assert.Equal(expected ? ["appContext", "boardContext", "cardContext"] : ["cardContext"], levels);
        // The block reaches the agent twice: in the launch prompt and in its get_board_card read.
        var blockGrowth = expected
            ? ContextLevels.Format([new("a", "App context", "Every app launch"), new("b", "Board context", "This board's message"), new("c", "Card context", "Card-level context")]).Length
              - ContextLevels.Format([new("c", "Card context", "Card-level context")]).Length
            : 0;
        Assert.Equal(baseline.Chars + 2 * blockGrowth, estimate.Chars);
        Assert.Equal(estimate.Contents.Where(part => levels.Contains(part.Key)).Sum(part => part.Chars),
            ContextLevels.Format(expected
                ? [new("a", "App context", "Every app launch"), new("b", "Board context", "This board's message"), new("c", "Card context", "Card-level context")]
                : [new("c", "Card context", "Card-level context")]).Length);

        var prompt = await BoardLaunchService.ComposePromptAsync(_boards, _root, card, "codex", null, "work", Ct,
            nudgeEnabled: ContextLevels.NudgeEnabledFor(settings, selection));
        var sample = await estimator.RecordLaunchAsync(_root, card, prompt, "work", null, "codex", selection, Ct);
        Assert.Equal(estimate.Chars, sample!.Chars);
        Assert.Equal(estimate.Sources.Single(part => part.Key == "prompt").Tokens, sample.PromptTokens);
    }

    [Fact]
    public async Task CardAgentGetsAppBoardCardAndCardTypeContextInOrderInOneBlock_AndGetBoardCardShowsTheSame()
    {
        var (board, lane, _, _) = await Lanes();
        var card = await Card(lane);
        await _boards.UpdateCardAsync(_root, card.Id, new(Type: "bug"), Ct);
        card = (await _boards.FindCardAsync(_root, card.Id, Ct))!;
        var service = new BoardService(_boards, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        await service.SaveAppContextSettingsAsync(new("APP level", 0), Ct);
        await service.SaveEveryContextSettingsAsync(_root, board, new("BOARD level", 0), Ct);
        await service.SaveContextSettingsAsync(_root, board, new(new("CARD level", [new("bug", "append", "TYPE level")]), 0), Ct);
        var expectedBlock = Heading + "\n\nApp context:\nAPP level\n\nBoard context:\nBOARD level\n\nCard context:\nCARD level\n\nCard type context (Bug):\nTYPE level";

        var nudged = await BoardLaunchService.ComposePromptAsync(_boards, _root, card, "codex", null, "work", Ct, nudgeEnabled: true);
        Assert.Equal(1, Occurrences(nudged.Prompt, expectedBlock));
        Assert.Equal(1, Occurrences(nudged.Prompt, Heading));
        // The block follows the fenced card data, where the user's board context always sat.
        Assert.True(nudged.Prompt.IndexOf("--- end card;", StringComparison.Ordinal) < nudged.Prompt.IndexOf(Heading, StringComparison.Ordinal));
        var detail = (await service.GetCardAsync(_root, card.Id, Ct))!;
        var read = await BoardTool.RenderCardAsync(service, _boards, _root, detail, BoardTool.CardReadOptions.Default, Ct, nudgeEnabled: true);
        Assert.EndsWith("\n\n" + expectedBlock, read.Text);

        // Nudge off: Card and Card type context still reach the agent; App and Board do not.
        var plain = await BoardLaunchService.ComposePromptAsync(_boards, _root, card, "codex", null, "work", Ct, nudgeEnabled: false);
        var cardOnly = Heading + "\n\nCard context:\nCARD level\n\nCard type context (Bug):\nTYPE level";
        Assert.Contains(cardOnly, plain.Prompt);
        Assert.DoesNotContain("APP level", plain.Prompt);
        Assert.DoesNotContain("BOARD level", plain.Prompt);
        read = await BoardTool.RenderCardAsync(service, _boards, _root, detail, BoardTool.CardReadOptions.Default, Ct, nudgeEnabled: false);
        Assert.EndsWith("\n\n" + cardOnly, read.Text);

        // Discussion carries the same block.
        var chat = await BoardLaunchService.ComposePromptAsync(_boards, _root, card, "codex", null, "chat", Ct, nudgeEnabled: true);
        Assert.Contains(expectedBlock, chat.Prompt);
        Assert.Contains(expectedBlock, BoardPromptComposer.ComposeDiscussion(card, null, null, ContextLevels.Format(chat.ContextLevels)));

        // Replace with an empty type message sends no card level; empty levels send nothing at all.
        await service.SaveContextSettingsAsync(_root, board, new(new("CARD level", [new("bug", "replace", "")]), 1), Ct);
        await service.SaveAppContextSettingsAsync(new("", 1), Ct);
        await service.SaveEveryContextSettingsAsync(_root, board, new("", 1), Ct);
        var empty = await BoardLaunchService.ComposePromptAsync(_boards, _root, card, "codex", null, "work", Ct, nudgeEnabled: true);
        Assert.DoesNotContain(Heading, empty.Prompt);
        Assert.DoesNotContain("CARD level", empty.Prompt);
        read = await BoardTool.RenderCardAsync(service, _boards, _root, detail, BoardTool.CardReadOptions.Default, Ct, nudgeEnabled: true);
        Assert.DoesNotContain(Heading, read.Text);
    }

    [Fact]
    public async Task AppContextIsGlobalRevisionedAndBounded_AndReachesLaunchesFromAnyProjectBoardOrNone()
    {
        var (board, _, _, _) = await Lanes();
        var service = new BoardService(_boards, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        Assert.Equal(new AppContextSettingsRecord("", 0), await service.GetAppContextSettingsAsync(Ct));
        Assert.Equal(new AppContextSettingsRecord("Everywhere", 1), await service.SaveAppContextSettingsAsync(new("  Everywhere  ", 0), Ct));
        await Assert.ThrowsAsync<BoardConflictException>(() => service.SaveAppContextSettingsAsync(new("stale", 0), Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => service.SaveAppContextSettingsAsync(new(new string('x', 4001), 1), Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => service.SaveAppContextSettingsAsync(new("missing revision", null), Ct));
        await service.SaveEveryContextSettingsAsync(_root, board, new("Board message", 0), Ct);

        static string Text(IReadOnlyList<ContextLevels.Level> levels) => ContextLevels.Format(levels);
        // The project's board, another project with no board, and no project at all.
        Assert.Equal(Heading + "\n\nApp context:\nEverywhere\n\nBoard context:\nBoard message", Text(await ContextLevels.ResolveNudgedAsync(_boards, _root, null, Ct)));
        Assert.Equal(Heading + "\n\nApp context:\nEverywhere", Text(await ContextLevels.ResolveNudgedAsync(_boards, _root + "-other", null, Ct)));
        Assert.Equal(Heading + "\n\nApp context:\nEverywhere", Text(await ContextLevels.ResolveNudgedAsync(_boards, null, null, Ct)));
        // A foreign board id never leaks its Board context into this project's launch.
        Assert.Equal(Heading + "\n\nApp context:\nEverywhere", Text(await ContextLevels.ResolveNudgedAsync(_boards, _root + "-other", board, Ct)));
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
        Assert.Equal("Other board", BoardLevel(await ContextLevels.ResolveNudgedAsync(_boards, _root, other.Id, Ct)));
        Assert.Equal("", BoardLevel(await ContextLevels.ResolveNudgedAsync(_boards, _root, board, Ct)));
        Assert.Null(BoardLevel(await ContextLevels.ResolveNudgedAsync(_boards, _root + "-foreign", other.Id, Ct)));

        static string? BoardLevel(IReadOnlyList<ContextLevels.Level> levels) =>
            levels.SingleOrDefault(level => level.Key == ContextLevels.BoardKey)?.Message;
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
