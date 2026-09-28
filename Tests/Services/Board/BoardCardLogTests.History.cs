using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardCardLogTests
{
    [Fact]
    public async Task History_UsesServerOrderDespiteAuthoredClockSkew_AndKeepsLocalEntriesFirst()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, NewCard("Initial"), Ct);
        await _store.UpdateCardAsync(_project, card.Id, new(Title: "First arrival"), Ct);
        await _store.UpdateCardAsync(_project, card.Id, new(Title: "Later arrival"), Ct);
        await _store.UpdateCardAsync(_project, card.Id, new(Title: "Offline draft"), Ct);
        var entries = await _store.GetCardHistoryAsync(_project, card.Id, Ct);

        await ExecuteAsync("UPDATE BoardComments SET RemoteSeq = 1, CreatedUTC = '2030-01-01T00:00:00Z' WHERE Id = $id", entries[0].Id);
        await ExecuteAsync("UPDATE BoardComments SET RemoteSeq = 2, CreatedUTC = '2040-01-01T00:00:00Z' WHERE Id = $id", entries[1].Id);
        await ExecuteAsync("UPDATE BoardComments SET RemoteSeq = 3, CreatedUTC = '2020-01-01T00:00:00Z' WHERE Id = $id", entries[2].Id);
        await ExecuteAsync("UPDATE BoardComments SET CreatedUTC = '2010-01-01T00:00:00Z' WHERE Id = $id", entries[3].Id);

        var page = await _store.GetHistoryAsync(_project, card.BoardId!, card.Id, 0, Ct);
        Assert.Equal(new[] { entries[3].Id, entries[2].Id, entries[1].Id, entries[0].Id }, page!.Select(x => x.Id));
        var nextPage = await _store.GetHistoryAsync(_project, card.BoardId!, card.Id, 2, Ct);
        Assert.Equal(new[] { entries[1].Id, entries[0].Id }, nextPage!.Select(x => x.Id));
        var boardHistory = await _store.GetHistoryAsync(_project, card.BoardId!, null, 0, Ct);
        Assert.Equal(page!.Select(x => x.Id), boardHistory!.Where(x => x.CardKey == card.Key).Select(x => x.Id));
        Assert.Equal(entries.Select(x => x.Id), (await _store.GetCardHistoryAsync(_project, card.Id, Ct)).Select(x => x.Id));
    }
}
