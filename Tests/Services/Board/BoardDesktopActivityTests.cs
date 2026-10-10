using Microsoft.Data.Sqlite;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardDesktopActivityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-desktop-activity-{Guid.NewGuid():N}");
    private readonly string _connectionString;
    private readonly BoardStore _store;
    private readonly string _project;
    private readonly string _otherProject;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardDesktopActivityTests()
    {
        Directory.CreateDirectory(_root);
        _project = Path.Combine(_root, "project-a");
        _otherProject = Path.Combine(_root, "project-b");
        _connectionString = $"Data Source={Path.Combine(_root, "board.db")};Pooling=False";
        _store = new BoardStore(_connectionString, _connectionString);
    }

    [Fact]
    public async Task MultipleClients_EndIndependently_AndRetainTheirRows()
    {
        var first = await CardAsync();
        var second = await CardAsync();
        var expiry = DateTime.UtcNow.AddMinutes(5);
        Assert.True(await _store.StartDesktopActivityAsync(_project, first.Id, "client-a", "Codex", expiry, Ct));
        Assert.True(await _store.StartDesktopActivityAsync(_project, first.Id, "client-b", "Claude", expiry, Ct));
        Assert.True(await _store.StartDesktopActivityAsync(_project, second.Id, "client-a", "Codex", expiry, Ct));
        await _store.EndDesktopActivityAsync("client-a", first.Id, Ct);
        Assert.Equal(2, (await ActiveAsync(first, second)).Count);
        await _store.EndDesktopActivityAsync("client-b", null, Ct);
        Assert.Equal(second.Id, Assert.Single(await ActiveAsync(first, second)));
        await _store.EndDesktopActivityAsync("client-a", null, Ct);
        Assert.Empty(await ActiveAsync(first, second));
        Assert.Equal(3L, await ScalarAsync("SELECT COUNT(*) FROM BoardDesktopActivity WHERE EndedUTC IS NOT NULL"));
    }

    [Fact]
    public async Task ScopedReadsAndStarts_ExcludeOtherProjects_UnrequestedAndDeletedCards()
    {
        var local = await CardAsync();
        var unrequested = await CardAsync();
        var foreign = await CardAsync(_otherProject);
        var expiry = DateTime.UtcNow.AddMinutes(5);
        Assert.False(await _store.StartDesktopActivityAsync(_project, foreign.Id, "client", "Codex", expiry, Ct));
        Assert.False(await _store.StartDesktopActivityAsync(_project, "missing", "client", "Codex", expiry, Ct));
        foreach (var card in new[] { local, unrequested })
            Assert.True(await _store.StartDesktopActivityAsync(Path.Combine(_project, "."), card.Id, "client", "Codex", expiry, Ct));
        Assert.True(await _store.StartDesktopActivityAsync(_otherProject, foreign.Id, "client", "Codex", expiry, Ct));

        var requested = Enumerable.Range(0, 110).Select(i => $"missing-{i}")
            .Concat([local.Id, foreign.Id, local.Id, "' OR 1=1 --"]).ToArray();
        Assert.Equal(local.Id, Assert.Single(await _store.GetDesktopActiveCardIdsAsync(Path.Combine(_project, "."), requested, DateTime.UtcNow, Ct)));
        Assert.Empty(await _store.GetDesktopActiveCardIdsAsync(_project, [], DateTime.UtcNow, Ct));
        await _store.DeleteCardAsync(_project, local.Id, Ct);
        Assert.Empty(await ActiveAsync(local));
        Assert.False(await _store.StartDesktopActivityAsync(_project, local.Id, "client", "Codex", expiry, Ct));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM BoardDesktopActivity WHERE CardId = $card AND EndedUTC IS NOT NULL", local.Id));
    }

    [Theory]
    [InlineData("Done")]
    [InlineData("Complete")]
    [InlineData("Completed")]
    [InlineData("Ship")]
    [InlineData("Shipped")]
    [InlineData("Closed")]
    [InlineData("  CLOSED work ")]
    public async Task ClosingThenReopening_DoesNotReviveActivityUntilExplicitStart(string laneName)
    {
        var card = await CardAsync();
        var closed = await _store.CreateColumnAsync(_project, laneName, "#999999", Ct);
        var expiry = DateTime.UtcNow.AddMinutes(5);
        await _store.StartDesktopActivityAsync(_project, card.Id, "client", "Codex", expiry, Ct);
        await _store.MoveCardAsync(_project, card.Id, closed.Id, null, Ct);
        Assert.Empty(await ActiveAsync(card));
        Assert.False(await _store.StartDesktopActivityAsync(_project, card.Id, "client", "Codex", expiry, Ct));
        await _store.MoveCardAsync(_project, card.Id, card.ColumnId, null, Ct);
        await _store.RenewDesktopActivityAsync("client", expiry, Ct);
        Assert.Empty(await ActiveAsync(card));
        Assert.True(await _store.StartDesktopActivityAsync(_project, card.Id, "client", "Codex Desktop", expiry, Ct));
        Assert.Equal(card.Id, Assert.Single(await ActiveAsync(card)));
        Assert.Equal("Codex Desktop", await ScalarAsync("SELECT ClientLabel FROM BoardDesktopActivity WHERE CardId = $card", card.Id));
    }

    [Fact]
    public async Task RenamingLaneClosed_EndsActivityEvenIfOlderWriterReopensIt()
    {
        var card = await CardAsync();
        await _store.StartDesktopActivityAsync(_project, card.Id, "client", "Codex", DateTime.UtcNow.AddMinutes(5), Ct);
        // Model an older writer which does not know the new activity table.
        await ExecuteAsync("UPDATE BoardColumns SET Name = 'Closed' WHERE Id = $card", card.ColumnId);
        await ExecuteAsync("UPDATE BoardColumns SET Name = 'Working' WHERE Id = $card", card.ColumnId);
        await _store.RenewDesktopActivityAsync("client", DateTime.UtcNow.AddMinutes(5), Ct);
        Assert.Empty(await ActiveAsync(card));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM BoardDesktopActivity WHERE EndedUTC IS NOT NULL"));
    }

    [Fact]
    public async Task LeaseExpiry_IsExclusive_AndRenewNeverRevivesExpiredOrEndedRows()
    {
        var active = await CardAsync();
        var expired = await CardAsync();
        var ended = await CardAsync();
        var expiry = DateTime.UtcNow.AddMinutes(5);
        foreach (var card in new[] { active, expired, ended })
            await _store.StartDesktopActivityAsync(_project, card.Id, "client", "Codex", expiry, Ct);
        Assert.Empty(await _store.GetDesktopActiveCardIdsAsync(_project, [active.Id], expiry, Ct));
        Assert.Equal(active.Id, Assert.Single(await _store.GetDesktopActiveCardIdsAsync(_project, [active.Id], expiry.AddTicks(-1), Ct)));
        Assert.False(await _store.StartDesktopActivityAsync(_project, active.Id, "past", "Codex", DateTime.UtcNow.AddMinutes(-1), Ct));
        await ExecuteAsync("UPDATE BoardDesktopActivity SET ExpiresUTC = '2000-01-01T00:00:00.0000000Z' WHERE CardId = $card", expired.Id);
        await _store.EndDesktopActivityAsync("client", ended.Id, Ct);
        await _store.RenewDesktopActivityAsync("client", expiry.AddMinutes(5), Ct);
        Assert.Equal(active.Id, Assert.Single(await _store.GetDesktopActiveCardIdsAsync(_project,
            [active.Id, expired.Id, ended.Id], expiry.AddMinutes(1), Ct)));
        Assert.Equal("2000-01-01T00:00:00.0000000Z", await ScalarAsync("SELECT ExpiresUTC FROM BoardDesktopActivity WHERE CardId = $card", expired.Id));
    }

    [Fact]
    public async Task Migration_IsAutomaticAdditive_AndLegacyCardWritesKeepWorking()
    {
        var card = await CardAsync();
        await ExecuteAsync("""
            DROP TRIGGER BoardCards_EndDesktopActivity;
            DROP TRIGGER BoardCards_DeleteDesktopActivity;
            DROP TRIGGER BoardColumns_EndDesktopActivity;
            DROP TABLE BoardDesktopActivity;
            DELETE FROM SchemaMigrations WHERE Component = 'board-desktop-activity';
            """);
        var upgraded = new BoardStore(_connectionString, _connectionString);
        Assert.NotNull(await upgraded.FindCardAsync(_project, card.Id, Ct));
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM SchemaMigrations WHERE Component = 'board-desktop-activity' AND Version = 1"));
        Assert.Equal(0L, await ScalarAsync("SELECT COUNT(*) FROM BoardDesktopActivity"));
        Assert.True(await upgraded.StartDesktopActivityAsync(_project, card.Id, "client", "Codex", DateTime.UtcNow.AddMinutes(5), Ct));
        await ExecuteAsync("UPDATE BoardCards SET Title = 'Legacy edit' WHERE Id = $card", card.Id);
        Assert.Equal("Legacy edit", (await upgraded.FindCardAsync(_project, card.Id, Ct))!.Title);
        await ExecuteAsync("UPDATE BoardCards SET DeletedUTC = '2026-01-01T00:00:00.0000000Z' WHERE Id = $card", card.Id);
        await ExecuteAsync("UPDATE BoardCards SET DeletedUTC = NULL WHERE Id = $card", card.Id);
        await upgraded.RenewDesktopActivityAsync("client", DateTime.UtcNow.AddMinutes(5), Ct);
        Assert.Empty(await ActiveAsync(card));
        // Opening another store is idempotent and retains ended activity rather than backfilling it.
        _ = new BoardStore(_connectionString, _connectionString);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM BoardDesktopActivity WHERE EndedUTC IS NOT NULL"));
    }

    private async Task<BoardCardRecord> CardAsync(string? project = null)
    {
        project ??= _project;
        await _store.EnsureDefaultColumnsAsync(project, Ct);
        return await _store.CreateCardAsync(project, new(null, "Desktop work", "", null, "medium", null, [], false), Ct);
    }

    private Task<IReadOnlySet<string>> ActiveAsync(params BoardCardRecord[] cards) =>
        _store.GetDesktopActiveCardIdsAsync(_project, cards.Select(c => c.Id).ToArray(), DateTime.UtcNow, Ct);

    private async Task ExecuteAsync(string sql, string? card = null)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (card is not null) command.Parameters.AddWithValue("$card", card);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<object?> ScalarAsync(string sql, string? card = null)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (card is not null) command.Parameters.AddWithValue("$card", card);
        return await command.ExecuteScalarAsync(Ct);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
