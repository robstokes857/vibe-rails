using Tests.Services.Integrations;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Diagnostics;
using VibeRails.Services.Integrations.VibeCodeRemote;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// Remote Boards behind the Pro plan: the scheduled sweep pauses with a visible reason and makes
/// no request while the plan says no, a denial mid-sweep stops the sweep, and a Board whose owner
/// is not Pro pauses alone and is asked again only after the bounded recheck.
/// </summary>
public sealed partial class BoardSyncServiceTests
{
    [Fact]
    public async Task AnAccountWithoutRemoteBoardsPausesItsPublishedBoardsOnce_AndMakesNoRequestOnAnyTick()
    {
        var card = await Card();
        await store.SetBoardSyncEnabledAsync(root, card.BoardId, true, Ct);
        await service.SyncDueAsync(Ct); // published while the plan allowed it
        var entitlements = new FakeAccountEntitlements();
        var log = new UploadFeatureLogRecorder();
        var gated = Gated(entitlements, log: log);
        entitlements.Off.Add(ProFeatures.RemoteBoards);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Edited while paused"), Ct);
        var calls = client.Calls;

        for (var tick = 0; tick < 3; tick++)
            await gated.SyncDueAsync(Ct);

        Assert.Equal(calls, client.Calls);
        var paused = (await gated.GetStatusAsync(root, card.BoardId, Ct))!;
        Assert.Equal(BoardSyncProPause.AccountMessage, paused.LastError);
        Assert.Equal("pro_required", paused.PausedCode);
        Assert.Equal("account", paused.PausedPayer);
        // Nothing is unpublished or dropped: the board stays enabled and its edit stays queued.
        Assert.True(paused.Enabled);
        Assert.True(paused.Published);
        Assert.Equal(1, paused.Unsent);
        // Said once, not once per tick.
        Assert.Single(log.Entries, entry => entry.Event == "pause");

        // The plan allows it again: the next tick sends the queued edit and clears the reason.
        entitlements.Off.Clear();
        await gated.SyncDueAsync(Ct);
        var resumed = (await gated.GetStatusAsync(root, card.BoardId, Ct))!;
        Assert.Null(resumed.LastError);
        Assert.Null(resumed.PausedCode);
        Assert.Null(resumed.PausedPayer);
        Assert.Equal(0, resumed.Unsent);
        Assert.True(client.Calls > calls);
    }

    [Fact]
    public async Task ThePlanIsNotAskedWhenNoBoardSyncs_AndAPlanThatAllowsItSyncsAsBefore()
    {
        var card = await Card();
        var entitlements = new FakeAccountEntitlements();
        var gated = Gated(entitlements);

        await gated.SyncDueAsync(Ct); // no board has opted in
        Assert.Equal(0, entitlements.Reads);

        await store.SetBoardSyncEnabledAsync(root, card.BoardId, true, Ct);
        await gated.SyncDueAsync(Ct);
        Assert.True(entitlements.Reads > 0);
        Assert.Null((await gated.GetStatusAsync(root, card.BoardId, Ct))!.LastError);
        Assert.Single(client.Entries);
    }

    [Fact]
    public async Task ADenialMidSweepStopsTheSweep_AndPausesTheOtherBoardsWithoutAskingForEach()
    {
        var (first, other, otherProject) = await TwoPublishedBoards();
        var entitlements = new FakeAccountEntitlements();
        var gated = Gated(entitlements);
        var refused = 0;
        client.BeforePush = _ =>
        {
            refused++;
            throw BoardSyncProPause.Failure(new ProDenial(ProFeatures.RemoteBoards, ProDenial.AccountPayer));
        };

        await gated.SyncDueAsync(Ct);
        await gated.SyncDueAsync(Ct);

        Assert.Equal(1, refused); // one refused request in all, not one per board or per tick
        Assert.Equal([ProFeatures.RemoteBoards], entitlements.Noted);
        foreach (var (project, boardId) in new[] { (root, first.BoardId), (otherProject, other.BoardId) })
        {
            var status = (await gated.GetStatusAsync(project, boardId, Ct))!;
            Assert.Equal(BoardSyncProPause.AccountMessage, status.LastError);
            Assert.Equal(("pro_required", "account"), (status.PausedCode, status.PausedPayer));
        }
    }

    [Fact]
    public async Task ADenialOfACardsActivityStopsThatUpload_InsteadOfTryingEveryOtherCard()
    {
        var first = await Card();
        await Card();
        await Card();
        await store.SetBoardSyncEnabledAsync(root, first.BoardId, true, Ct);
        var entitlements = new FakeAccountEntitlements();
        var gated = Gated(entitlements);
        client.ActivityError = BoardSyncProPause.Failure(new ProDenial(ProFeatures.RemoteBoards, ProDenial.AccountPayer));

        await gated.SyncDueAsync(Ct);
        await gated.SyncDueAsync(Ct);

        Assert.Equal(1, client.ActivityAttempts); // three cards were due; one refusal was enough
        var status = (await gated.GetStatusAsync(root, first.BoardId, Ct))!;
        Assert.Equal(BoardSyncProPause.AccountMessage, status.LastError);
        Assert.Equal(("pro_required", "account"), (status.PausedCode, status.PausedPayer));
        Assert.Equal([ProFeatures.RemoteBoards], entitlements.Noted);
    }

    [Fact]
    public async Task ABoardWhoseOwnerIsNotProPausesAlone_AndIsAskedAgainOnlyAfterTheRecheck()
    {
        var (first, other, otherProject) = await TwoPublishedBoards();
        var entitlements = new FakeAccountEntitlements();
        var time = new ManualTime();
        var gated = Gated(entitlements, new BoardSyncProPause(time));
        var ownerRefusals = 0;
        var otherPushes = 0;
        var refuse = true;
        client.BeforePush = request =>
        {
            if (request.SourceRepository == "other-project") { otherPushes++; return; }
            if (!refuse) return;
            ownerRefusals++;
            throw BoardSyncProPause.Failure(new ProDenial(ProFeatures.RemoteBoards, ProDenial.BoardOwnerPayer));
        };

        await gated.SyncDueAsync(Ct);

        var paused = (await gated.GetStatusAsync(root, first.BoardId, Ct))!;
        Assert.Equal(BoardSyncProPause.BoardOwnerMessage, paused.LastError);
        Assert.Equal(("pro_required", "board_owner"), (paused.PausedCode, paused.PausedPayer));
        // The member's own plan is untouched, so their other boards keep syncing.
        Assert.Empty(entitlements.Noted);
        Assert.Null((await gated.GetStatusAsync(otherProject, other.BoardId, Ct))!.LastError);
        Assert.Equal((1, 1), (ownerRefusals, otherPushes));

        // The next ticks leave the refused board alone and still sync the other one.
        await gated.SyncDueAsync(Ct);
        await gated.SyncDueAsync(Ct);
        Assert.Equal((1, 3), (ownerRefusals, otherPushes));

        // About fifteen minutes later it is asked again, once.
        time.Advance(BoardSyncProPause.OwnerRecheck + TimeSpan.FromSeconds(1));
        await gated.SyncDueAsync(Ct);
        await gated.SyncDueAsync(Ct);
        Assert.Equal(2, ownerRefusals);

        // The owner renewed: the next recheck syncs and clears the reason.
        refuse = false;
        time.Advance(BoardSyncProPause.OwnerRecheck + TimeSpan.FromSeconds(1));
        await gated.SyncDueAsync(Ct);
        var resumed = (await gated.GetStatusAsync(root, first.BoardId, Ct))!;
        Assert.Null(resumed.LastError);
        Assert.Null(resumed.PausedCode);
    }

    [Fact]
    public async Task APublishTheUserAskedForReportsTheDenialTyped_SoTheRouteCanOfferUpgrade()
    {
        var card = await Card();
        client.BeforePublish = () => throw BoardSyncProPause.Failure(new ProDenial(ProFeatures.RemoteBoards, ProDenial.AccountPayer));

        var error = await Assert.ThrowsAsync<BoardSyncClientException>(() => service.SetPublishedAsync(root, card.BoardId, true, Ct));

        Assert.Equal(("pro_required", "remote_boards", "account"), (error.Code, error.Feature, error.Payer));
        Assert.Equal(BoardSyncProPause.AccountMessage, error.Message);
        // Any other publish failure keeps its existing validation wording.
        client.BeforePublish = () => throw new BoardSyncClientException("Temporary outage", "network");
        var outage = await Assert.ThrowsAsync<BoardValidationException>(() => service.SyncNowAsync(root, card.BoardId, Ct));
        Assert.StartsWith("Publishing failed:", outage.Message);
    }

    [Theory]
    [InlineData(BoardSyncProPause.AccountMessage, "account")]
    [InlineData(BoardSyncProPause.BoardOwnerMessage, "board_owner")]
    [InlineData("viberails.ai rejected the API key. Check it in Settings.", null)]
    [InlineData("Publishing failed: " + BoardSyncProPause.AccountMessage, null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ThePausedPayerIsDerivedFromTheStoredReasonOnly(string? lastError, string? payer)
    {
        Assert.Equal(payer, BoardSyncProPause.PayerOf(lastError));
    }

    private BoardSyncService Gated(FakeAccountEntitlements entitlements, BoardSyncProPause? pause = null, IFeatureLog? log = null) =>
        new(store, client, new BoardSyncLock(Path.Combine(root, "sync.lock")), log ?? NullFeatureLog.Instance, activityCache,
            null, entitlements, pause ?? new BoardSyncProPause());

    private async Task<(BoardCardRecord First, BoardCardRecord Other, string OtherProject)> TwoPublishedBoards()
    {
        var first = await Card();
        var otherProject = Path.Combine(root, "other-project");
        await store.EnsureDefaultColumnsAsync(otherProject, Ct);
        var other = await store.CreateCardAsync(otherProject, new(null, "Another project", "", null, "medium", null, [], false), Ct);
        await store.SetBoardSyncEnabledAsync(root, first.BoardId, true, Ct);
        await store.SetBoardSyncEnabledAsync(otherProject, other.BoardId, true, Ct);
        await service.SyncDueAsync(Ct);
        Assert.Null((await service.GetStatusAsync(root, first.BoardId, Ct))!.LastError);
        Assert.Null((await service.GetStatusAsync(otherProject, other.BoardId, Ct))!.LastError);
        return (first, other, otherProject);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}
