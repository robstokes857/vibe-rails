using Moq;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSyncServiceTests
{
    [Fact]
    public async Task RemoteLaunchRejectsProtectedInstructionsButAllowsAnAcknowledgedCorrection()
    {
        var card = await Card();
        var other = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Rejected title", Description: "Rejected local instructions"), Ct);
        client.RejectEntryId = Assert.Single(await store.GetUnsentLogEntriesAsync(card.BoardId, 20, Ct)).Entry.Id;
        client.Web(card, "change", "Saved hosted instructions", """{"description":{"to":"Hosted instructions"}}""");
        var required = client.Entries[^1].Seq;
        var launcher = RemoteLauncher();
        Assert.Equal("sync_failed", (await RemoteStart(card, required, launcher)).Status);
        launcher.VerifyNoOtherCalls();
        var status = (await service.GetStatusAsync(root, card.BoardId, Ct))!;
        Assert.Null(status.LastError);
        Assert.Equal(0, status.Unsent);
        Assert.True(status.Cursor >= required);
        Assert.Equal(1, status.Rejected);
        Assert.Equal("Rejected local instructions", (await store.FindCardAsync(root, card.Id, Ct))!.Description);
        // Another card's rejected instructions cannot block this card.
        Assert.Equal("started", (await RemoteStart(other, required, launcher)).Status);
        await store.UpdateCardAsync(root, card.Id, new(Description: "Corrected instructions"), Ct);
        Assert.Equal("sync_failed", (await RemoteStart(card, required, launcher)).Status);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Corrected title"), Ct);
        Assert.Equal("started", (await RemoteStart(card, required, launcher)).Status);
        Assert.Equal(1, (await service.GetStatusAsync(root, card.BoardId, Ct))!.Rejected);
        Assert.Empty(await store.GetFieldsChangedAfterAsync(card.Id, long.MaxValue, Ct));
        Assert.Equal("Corrected instructions", (await store.FindCardAsync(root, card.Id, Ct))!.Description);
    }

    [Theory]
    [InlineData("change", """{"description":{"to":"New instructions"},"priority":{"to":"future-priority"}}""")]
    [InlineData("future_kind", """{"description":{"to":"New instructions"}}""")]
    public async Task RemoteLaunchRejectsTargetSkipsBeyondTheStatusPreviewButNotOtherCards(string kind, string changes)
    {
        var target = await Card(); var noisy = await Card(); var safe = await Card();
        await service.SetPublishedAsync(root, target.BoardId, true, Ct);
        client.Web(target, kind, "Unsupported target change", changes);
        for (var i = 0; i < 60; i++) client.Web(noisy, "future_kind", "Another card's change", null);
        var required = client.Entries[^1].Seq;
        var launcher = RemoteLauncher();
        Assert.Equal("sync_failed", (await RemoteStart(target, required, launcher)).Status);
        launcher.VerifyNoOtherCalls();
        var status = (await service.GetStatusAsync(root, target.BoardId, Ct))!;
        Assert.Null(status.LastError);
        Assert.Equal(required, status.Cursor);
        Assert.Equal(0, status.Unsent);
        Assert.Equal(61, status.Skipped);
        Assert.DoesNotContain(status.SkippedEntries!, entry => entry.CardKey == target.Key);
        Assert.Equal("started", (await RemoteStart(safe, required, launcher)).Status);
    }

    private Task<BoardLaunchResult> RemoteStart(BoardCardRecord card, long required, Mock<IBoardLaunchService> launcher) =>
        new BoardRemoteLaunchService(store, client, service, launcher.Object).LaunchAsync(root, Guid.NewGuid(),
            new(Guid.NewGuid(), client.RemoteId, card.Id, required), client.DestinationKey!, Ct);

    [Fact]
    public async Task CardSyncApplicationCheckScopesIdentitySequenceAndPendingFields()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        Assert.True(await store.IsCardSyncAppliedAsync(root, card.BoardId, card.Id, 1, Ct));
        Assert.False(await store.IsCardSyncAppliedAsync(root + "-other", card.BoardId, card.Id, 1, Ct));
        Assert.False(await store.IsCardSyncAppliedAsync(root, "other-board", card.Id, 1, Ct));
        Assert.False(await store.IsCardSyncAppliedAsync(root, card.BoardId, card.Key, 1, Ct));
        Assert.False(await store.IsCardSyncAppliedAsync(root, card.BoardId, card.Id, -1, Ct));
        client.Web(card, "future_kind", "Not yet applied", null);
        await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.True(await store.IsCardSyncAppliedAsync(root, card.BoardId, card.Id, 1, Ct));
        Assert.False(await store.IsCardSyncAppliedAsync(root, card.BoardId, card.Id, 2, Ct));
        await store.UpdateCardAsync(root, card.Id, new(Description: "Pending instructions"), Ct);
        Assert.False(await store.IsCardSyncAppliedAsync(root, card.BoardId, card.Id, 1, Ct));
    }

    private Mock<IBoardLaunchService> RemoteLauncher()
    {
        var launcher = new Mock<IBoardLaunchService>(MockBehavior.Strict);
        launcher.Setup(x => x.LaunchAsync(root, It.IsAny<string>(), null, Ct, "work"))
            .ReturnsAsync(new LaunchBoardCardResponse("tab", Guid.NewGuid().ToString(), "codex", root, "card", "VB-1", "codex"));
        return launcher;
    }
}
