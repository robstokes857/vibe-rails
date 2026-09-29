using System.Text.Json;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Diagnostics;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardSyncServiceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-sync-service-" + Guid.NewGuid().ToString("N"));
    private readonly BoardStore store;
    private readonly FakeClient client = new();
    private readonly BoardSyncService service;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string cs;

    public BoardSyncServiceTests()
    {
        Directory.CreateDirectory(root);
        cs = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        store = new BoardStore(cs, cs);
        service = new(store, client, new BoardSyncLock(Path.Combine(root, "sync.lock")), NullFeatureLog.Instance);
    }

    private async Task<BoardCardRecord> Card()
    {
        await store.EnsureDefaultColumnsAsync(root, Ct);
        return await store.CreateCardAsync(root, new(null, "Local", "description @src/file.cs", "env:7:codex", "medium", null, [], false), Ct);
    }

    [Fact]
    public async Task DisabledBoardsSendNothing_AndPauseRetainsTheRemoteCopy()
    {
        var card = await Card();
        await service.SyncDueAsync(Ct);
        Assert.Equal(0, client.Calls);
        var status = await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        Assert.True(status!.Enabled);
        Assert.Null(status.LastError);
        Assert.Equal(0, status.Unsent);
        var created = Assert.Single(client.Entries);
        Assert.Equal("base:codex", created.Changes!.Value.GetProperty("assignee").GetProperty("to").GetString());
        Assert.DoesNotContain("env:7", JsonSerializer.Serialize(created, BoardSyncJsonContext.Default.BoardSyncPulledEntryWire));
        Assert.Equal("env:7:codex", (await store.FindCardAsync(root, card.Id, Ct))!.Assignee);
        Assert.Contains("@src/file.cs", created.Changes.Value.GetRawText());
        await service.SetPublishedAsync(root, card.BoardId, false, Ct);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Offline"), Ct);
        var calls = client.Calls;
        await service.SyncDueAsync(Ct);
        Assert.Equal(calls, client.Calls);
        Assert.Single(client.Entries);
    }

    [Fact]
    public async Task LostAcknowledgementRetriesSameEntryWithoutDuplicatingIt()
    {
        var card = await Card();
        client.LoseNextAck = true;
        var first = await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        Assert.NotNull(first!.LastError);
        Assert.Equal(1, first.Unsent);
        var second = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(second!.LastError);
        Assert.Equal(0, second.Unsent);
        Assert.Single(client.Entries);
    }

    [Fact]
    public async Task ServerArrivalWinsPerField_AndHistoryRetainsTheLosingEdit()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.Web(card, "change", "Web changed title and description", """{"title":{"to":"Web"},"description":{"to":"Web description"}}""");
        await store.UpdateCardAsync(root, card.Id, new(Title: "Offline local title"), Ct);
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(status!.LastError);
        var current = await store.FindCardAsync(root, card.Id, Ct);
        Assert.Equal("Offline local title", current!.Title);
        Assert.Equal("Web description", current.Description);
        var history = await store.GetHistoryAsync(root, card.BoardId, card.Id, 0, Ct);
        Assert.Contains(history!, e => e.Body == "Web changed title and description" && e.Changes!.Contains("Web"));
        Assert.Equal(0, status.Unsent);
        Assert.Equal(3, status.Cursor);
    }

    [Fact]
    public async Task EditsWhilePullingRemainQueuedAndWinOnTheNextPush()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.Web(card, "change", "Web", """{"title":{"to":"Web"}}""");
        client.BeforePull = async () => { await store.UpdateCardAsync(root, card.Id, new(Title: "During exchange"), Ct); };
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Equal("During exchange", (await store.FindCardAsync(root, card.Id, Ct))!.Title);
        Assert.Equal(1, status!.Unsent);
        Assert.Null((await service.SyncNowAsync(root, card.BoardId, Ct))!.LastError);
        Assert.Equal(0, await store.CountUnsentLogEntriesAsync(card.BoardId, Ct));
    }

    [Fact]
    public async Task RejectedOrInvalidAcknowledgementsNeverDiscardQueuedWrites()
    {
        var card = await Card();
        client.BadAck = true;
        var first = await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        Assert.NotNull(first!.LastError);
        Assert.Equal(1, first.Unsent);
        client.BadAck = false;
        client.Reject = true;
        Assert.NotNull((await service.SyncNowAsync(root, card.BoardId, Ct))!.LastError);
        Assert.Equal(1, await store.CountUnsentLogEntriesAsync(card.BoardId, Ct));
        client.Reject = false;
        Assert.Null((await service.SyncNowAsync(root, card.BoardId, Ct))!.LastError);
    }

    [Fact]
    public async Task RejectedChangeIsRetainedAndProtectedWhileLaterWritesContinue_ThenCorrectionReleasesItsFields()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Rejected title", Description: "Rejected description"), Ct);
        var rejected = Assert.Single(await store.GetUnsentLogEntriesAsync(card.BoardId, 20, Ct));
        client.RejectEntryId = rejected.Entry.Id;
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Later valid comment", Ct);
        client.Web(card, "change", "Concurrent web change", """{"title":{"to":"Web title"},"description":{"to":"Web description"}}""");
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(status!.LastError);
        Assert.Equal(0, status.Unsent);
        Assert.Equal(1, status.Rejected);
        Assert.Equal(rejected.Entry.Id, Assert.Single(status.RejectedEntries!).EntryId);
        Assert.Contains(client.Entries, e => e.Body == "Later valid comment");
        Assert.Equal("Rejected title", (await store.FindCardAsync(root, card.Id, Ct))!.Title);
        Assert.True(await store.HasLogEntryAsync(rejected.Entry.Id, Ct));

        await store.UpdateCardAsync(root, card.Id, new(Title: "Local correction"), Ct);
        await service.SyncNowAsync(root, card.BoardId, Ct);
        client.Web(card, "change", "After correction", """{"title":{"to":"New web title"},"description":{"to":"New web description"}}""");
        await service.SyncNowAsync(root, card.BoardId, Ct);
        var current = await store.FindCardAsync(root, card.Id, Ct);
        Assert.Equal("New web title", current!.Title);
        Assert.Equal("Rejected description", current.Description);
        Assert.Equal(1, (await service.GetStatusAsync(root, card.BoardId, Ct))!.Rejected);
    }

    [Fact]
    public async Task RejectedCreationKeepsDependentEditsLocalWithoutBlockingOtherCards()
    {
        var card = await Card();
        var creation = Assert.Single(await store.GetUnsentLogEntriesAsync(card.BoardId, 20, Ct));
        client.RejectEntryId = creation.Entry.Id;
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "Must stay local", Ct);
        var other = await Card();
        var status = await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        Assert.Null(status!.LastError);
        Assert.Equal(1, status.Rejected);
        Assert.Equal(other.Id, Assert.Single(client.Entries).CardId);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Still local"), Ct);
        await store.AddCommentAsync(root, other.Id, BoardAuthor.User(), "New valid comment", Ct);
        await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.DoesNotContain(client.Entries, e => e.CardId == card.Id);
        Assert.Contains(client.Entries, e => e.Body == "New valid comment");
        Assert.Equal("Must stay local", Assert.Single((await store.GetCardDetailAsync(root, card.Id, Ct))!.Comments).Body);
    }

    [Fact]
    public async Task RejectionCannotNameAnEntryOutsideTheAttemptedBatch()
    {
        var card = await Card();
        client.RejectUnknownEntry = true;
        var status = await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        Assert.NotNull(status!.LastError);
        Assert.Equal(1, status.Unsent);
        Assert.Equal(0, status.Rejected);
        Assert.Single(await store.GetUnsentLogEntriesAsync(card.BoardId, 20, Ct));
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("zero")]
    [InlineData("above_last")]
    [InlineData("last_behind_cursor")]
    [InlineData("sequence_order")]
    [InlineData("id_order")]
    public async Task InvalidSequenceAcknowledgementsLeaveTheWholeBatchQueued(string invalid)
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        await store.UpdateCardAsync(root, card.Id, new(Title: "First"), Ct);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Second"), Ct);
        client.TransformAck = response => invalid switch
        {
            "duplicate" => response with { Accepted = [response.Accepted[0], response.Accepted[1] with { Seq = response.Accepted[0].Seq }] },
            "zero" => response with { Accepted = [response.Accepted[0] with { Seq = 0 }, response.Accepted[1]] },
            "above_last" => response with { Accepted = [response.Accepted[0], response.Accepted[1] with { Seq = response.LastSeq + 1 }] },
            "last_behind_cursor" => response with { LastSeq = 0 },
            "sequence_order" => response with { Accepted = [response.Accepted[0] with { Seq = response.Accepted[1].Seq }, response.Accepted[1] with { Seq = response.Accepted[0].Seq }] },
            "id_order" => response with { Accepted = response.Accepted.AsEnumerable().Reverse().ToList() },
            _ => response
        };
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Contains("invalid sync acknowledgement", status!.LastError);
        Assert.Equal(2, status.Unsent);
        Assert.Equal(1, status.Cursor);
        client.TransformAck = null;
        Assert.Null((await service.SyncNowAsync(root, card.BoardId, Ct))!.LastError);
    }

    [Fact]
    public async Task RetryAllowsNewCreationBeforeOlderAcknowledgedChanges()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Accepted before disconnect"), Ct);
        client.LoseNextAck = true;
        Assert.NotNull((await service.SyncNowAsync(root, card.BoardId, Ct))!.LastError);
        var other = await Card();
        await store.AddCommentAsync(root, card.Id, BoardAuthor.User(), "After disconnect", Ct);
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(status!.LastError);
        Assert.Equal(0, status.Unsent);
        Assert.Equal(3, client.Entries.Single(e => e.CardId == other.Id).Seq);
        Assert.Equal(4, client.Entries.Count);
    }

    [Fact]
    public async Task ReplayAcknowledgementsMayPrecedeCursorAndAdvertisedLastSequence()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        await store.ResetSentMarksAsync(card.BoardId, Ct);
        client.TransformAck = response => response with { LastSeq = response.LastSeq + 10 };
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(status!.LastError);
        Assert.Equal(0, status.Unsent);
        Assert.Equal(1, status.Cursor);
        Assert.Single(client.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LaterBatchCannotReuseAnEarlierAcknowledgementOrRegressItsHighWater(bool regressHighWater)
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        for (var index = 0; index < 21; index++)
            await store.UpdateCardAsync(root, card.Id, new(Title: "Edit " + index), Ct);
        var batch = 0;
        client.TransformAck = response => ++batch == 1
            ? response with { LastSeq = regressHighWater ? 100 : response.LastSeq }
            : regressHighWater ? response : response with { Accepted = [response.Accepted[0] with { Seq = 2 }] };
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Contains("invalid sync acknowledgement", status!.LastError);
        Assert.Equal(1, status.Unsent);
        Assert.Equal(1, status.Cursor);
        Assert.Equal(21, await store.GetMaxAcknowledgedSequenceAsync(card.BoardId, Ct));
        // Even after the failed pass, persisted ACKs still protect the next attempt against
        // sequence reuse and a LastSeq below the previous successful batch's records.
        client.TransformAck = response => response with
        {
            Accepted = [response.Accepted[0] with { Seq = 2 }],
            LastSeq = regressHighWater ? 2 : response.LastSeq
        };
        var retried = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Contains("invalid sync acknowledgement", retried!.LastError);
        Assert.Equal(1, retried.Unsent);
        client.TransformAck = null;
        Assert.Null((await service.SyncNowAsync(root, card.BoardId, Ct))!.LastError);
    }

    [Fact]
    public async Task EmptyPageDoesNotSkipToTheAdvertisedSequence_AndUnknownKindsArePassedOverAndCounted()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.AdvertisedLastSeq = 99;
        var empty = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(empty!.LastError);
        Assert.Equal(1, empty.Cursor);
        client.AdvertisedLastSeq = null;
        client.Web(card, "restored", "Declared but not applied by this version", null);
        client.Web(card, "future_kind", "Unsupported", null);
        client.Web(card, "comment", "After the unsupported entries", null);
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(status!.LastError);
        Assert.Equal(4, status.Cursor);
        Assert.Equal("After the unsupported entries", Assert.Single((await store.GetCardDetailAsync(root, card.Id, Ct))!.Comments).Body);
        Assert.Equal(2, status.Skipped);
        Assert.Equal(["future_kind", "restored"], status.SkippedEntries!.Select(e => e.Kind));
        Assert.All(status.SkippedEntries!, e => Assert.Contains("cannot apply remote entry kind", e.Reason));
        Assert.Equal(client.Entries[2].Id, status.SkippedEntries![0].EntryId);
        Assert.Equal(card.Key, status.SkippedEntries![0].CardKey);

        // The version that skipped an entry does not try it again: the next sync pulls from the cursor.
        var calls = client.PullAfters.Count;
        var again = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Equal(4, Assert.Single(client.PullAfters.Skip(calls)));
        Assert.Equal(2, again!.Skipped);
    }

    [Theory]
    [InlineData("first-gap")]
    [InlineData("later-gap")]
    [InlineData("missing-tail")]
    public async Task SequenceGapsArePassedOverInsteadOfStoppingEveryLaterTick(string gap)
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.Web(card, "change", "First", """{"title":{"to":"First"}}""");
        client.Web(card, "change", "Second", """{"title":{"to":"Second"}}""");
        client.Web(card, "change", "Third", """{"title":{"to":"Third"}}""");
        // The server no longer serves one sequence: the first after the cursor, one in the middle, or the newest.
        var missing = gap switch { "first-gap" => 2, "later-gap" => 3, "missing-tail" => 4, _ => throw new InvalidOperationException() };
        client.Entries.RemoveAll(e => e.Seq == missing);
        client.AdvertisedLastSeq = 4;
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(status!.LastError);
        Assert.Equal(missing == 4 ? 3 : 4, status.Cursor);
        Assert.Equal(missing == 4 ? "Second" : "Third", (await store.FindCardAsync(root, card.Id, Ct))!.Title);
        Assert.Equal(0, status.Skipped);
        var again = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(again!.LastError);
        Assert.Equal(status.Cursor, again.Cursor);
    }

    [Theory]
    [InlineData("beyond-snapshot")]
    [InlineData("gap-beyond-snapshot")]
    [InlineData("duplicate")]
    [InlineData("out-of-order")]
    [InlineData("regressed-snapshot")]
    [InlineData("false-has-more")]
    [InlineData("empty-has-more")]
    [InlineData("null-entry")]
    public async Task MalformedPullPagesDoNotApplyAnyEntriesOrAdvanceTheCursor(string invalid)
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.Web(card, "change", "First", """{"title":{"to":"First"}}""");
        client.Web(card, "change", "Second", """{"title":{"to":"Second"}}""");
        client.TransformPull = response => invalid switch
        {
            "beyond-snapshot" => response with { LastSeq = 2 },
            "gap-beyond-snapshot" => response with { Entries = [response.Entries[0] with { Seq = 100 }], LastSeq = 2 },
            "duplicate" => response with { Entries = [response.Entries[0], response.Entries[1] with { Seq = 2 }] },
            "out-of-order" => response with { Entries = [response.Entries[1], response.Entries[0]] },
            "regressed-snapshot" => response with { Entries = [], LastSeq = 0 },
            "false-has-more" => response with { HasMore = true },
            "empty-has-more" => response with { Entries = [], HasMore = true },
            "null-entry" => response with { Entries = [null!] },
            _ => throw new InvalidOperationException()
        };
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Contains("invalid sync page", status!.LastError);
        Assert.Equal(1, status.Cursor);
        Assert.Equal(1, (await store.GetSyncLinkAsync(root, card.BoardId, Ct))!.Cursor);
        Assert.Equal("Local", (await store.FindCardAsync(root, card.Id, Ct))!.Title);
        foreach (var entry in client.Entries.Skip(1)) Assert.False(await store.HasLogEntryAsync(entry.Id, Ct));
        client.TransformPull = null;
        var retried = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(retried!.LastError);
        Assert.Equal(3, retried.Cursor);
        Assert.Equal("Second", (await store.FindCardAsync(root, card.Id, Ct))!.Title);
    }

    [Fact]
    public async Task PullFailureOnALaterPageKeepsTheCursorOfPagesAlreadyApplied()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        for (var index = 0; index < 20; index++)
            client.Web(card, "comment", "Web " + index, null);
        client.Web(card, "comment", "Malformed", null);
        client.Entries[^1] = client.Entries[^1] with { Author = client.Entries[^1].Author with { Kind = "unknown" } };
        client.Web(card, "comment", "After the malformed entry", null);

        // Page one (sequences 2..21) applies and persists its cursor; page two fails validation.
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.NotNull(status!.LastError);
        Assert.Equal(21, status.Cursor);
        Assert.Equal(21, (await store.GetSyncLinkAsync(root, card.BoardId, Ct))!.Cursor);
        Assert.Equal(20, (await store.GetCardDetailAsync(root, card.Id, Ct))!.Comments.Count);

        // The next tick resumes after page one instead of re-fetching it.
        client.PullAfters.Clear();
        Assert.NotNull((await service.SyncNowAsync(root, card.BoardId, Ct))!.LastError);
        Assert.Equal(21, Assert.Single(client.PullAfters));
        Assert.Equal(20, (await store.GetCardDetailAsync(root, card.Id, Ct))!.Comments.Count);
    }

    [Fact]
    public async Task RemoteBoardGoneIsReportedWithRepublishGuidance_AndPublishingStaysOn()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        await store.UpdateCardAsync(root, card.Id, new(Title: "Edited after the remote copy was deleted"), Ct);
        client.PushError = new BoardSyncClientException(
            "viberails.ai no longer has this board's published copy. Turn publishing off and on to publish it again.",
            BoardSyncWire.CodeBoardNotFound, 404);
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Contains("off and on", status!.LastError);
        Assert.True(status.Enabled);
        Assert.Equal(1, status.Unsent);

        // The documented recovery: switch publishing off and on, which publishes again and resumes.
        client.PushError = null;
        await service.SetPublishedAsync(root, card.BoardId, false, Ct);
        var republished = await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        Assert.Null(republished!.LastError);
        Assert.Equal(0, republished.Unsent);
    }

    [Fact]
    public async Task ShortContiguousPagesCanContinueToANewerSnapshot()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.Web(card, "comment", "First", null);
        client.Web(card, "comment", "Second", null);
        var pages = 0;
        client.TransformPull = response =>
        {
            if (++pages == 1) client.Web(card, "comment", "Arrived after snapshot", null);
            return response with { Entries = response.Entries.Take(1).ToList(), HasMore = response.Entries.Count > 1 };
        };
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(status!.LastError);
        Assert.Equal(4, status.Cursor);
        Assert.Equal(3, pages);
        Assert.Equal(3, (await store.GetCardDetailAsync(root, card.Id, Ct))!.Comments.Count);
        Assert.Null((await service.SyncNowAsync(root, card.BoardId, Ct))!.LastError);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"title\":{\"to\":\"Remote\"}}")]
    [InlineData("{\"lane\":{\"to\":\"col_missing\"}}")]
    public async Task RemoteCreationRequiresTitleAndLane(string changes)
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.Web(card, "created", "Created", changes);
        var entry = client.Entries[^1] with { CardId = "card_000000000099", CardKey = "VB-ABCDE-99" };
        client.Entries[^1] = entry;
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(status!.LastError);
        Assert.Equal(2, status.Cursor);
        Assert.Contains("requires title and lane", Assert.Single(status.SkippedEntries!).Reason);
        Assert.Null(await store.FindCardAsync(root, entry.CardId, Ct));
        Assert.False(await store.HasLogEntryAsync(entry.Id, Ct));
    }

    public static IEnumerable<object[]> InvalidFieldChanges()
    {
        string[] fields =
        [
            "\"type\":{\"to\":\"\"}", "\"type\":{\"to\":\"spike\"}", "\"type\":{\"to\":\"TASK\"}",
            "\"priority\":{\"to\":\" \"}", "\"priority\":{\"to\":\"urgent\"}", "\"priority\":{\"to\":\"HIGH\"}",
            "\"lane\":{\"to\":\"bad lane\"}", "\"lane\":{\"to\":\"_lane\"}",
            "\"lane\":{\"to\":\"" + new string('x', 65) + "\"}",
            "\"assignee\":{\"to\":\"env:7:codex\"}", "\"assignee\":{\"to\":\"base:CODEX\"}",
            "\"assignee\":{\"to\":\" base:codex \"}", "\"points\":{\"to\":4}",
            "\"blocked\":{\"to\":\"true\"}", "\"flagged\":{\"to\":1}",
            "\"tags\":{\"to\":[\"" + new string('x', 41) + "\"]}", "\"description\":{}"
        ];
        foreach (var kind in new[] { "created", "change" })
            foreach (var field in fields)
                yield return [kind, "{\"title\":{\"to\":\"Remote\"},\"lane\":{\"to\":\"col_missing\"}," + field + "}"];
    }

    [Theory]
    [MemberData(nameof(InvalidFieldChanges))]
    public async Task InvalidRemoteFieldsNeverBecomeAppliedState(string kind, string changes)
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.Web(card, kind, "Remote", changes);
        var entry = client.Entries[^1];
        if (kind == "created") client.Entries[^1] = entry = entry with { CardId = "card_000000000099", CardKey = "VB-ABCDE-99" };
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        // A value this version does not accept is passed over and counted, never applied.
        Assert.Null(status!.LastError);
        Assert.Equal(2, status.Cursor);
        Assert.Equal(entry.Id, Assert.Single(status.SkippedEntries!).EntryId);
        Assert.False(await store.HasLogEntryAsync(entry.Id, Ct));
        Assert.Equal("Local", (await store.FindCardAsync(root, card.Id, Ct))!.Title);
        if (kind == "created") Assert.Null(await store.FindCardAsync(root, entry.CardId, Ct));
    }

    [Theory]
    [InlineData("entry-id")]
    [InlineData("author-kind")]
    [InlineData("author-label")]
    [InlineData("author-cli")]
    [InlineData("summary")]
    [InlineData("changes-bytes")]
    [InlineData("key-case")]
    [InlineData("key-number")]
    public async Task InvalidWireMetadataStopsBeforeWriting(string invalid)
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.Web(card, "change", "Remote", """{"title":{"to":"Remote"}}""");
        var entry = client.Entries[^1];
        client.Entries[^1] = entry = invalid switch
        {
            "entry-id" => entry with { Id = "bad id" },
            "author-kind" => entry with { Author = entry.Author with { Kind = "unknown" } },
            "author-label" => entry with { Author = entry.Author with { Label = new string('x', 201) } },
            "author-cli" => entry with { Author = entry.Author with { Cli = new string('x', 65) } },
            "summary" => entry with { Body = new string('x', 2001) },
            "changes-bytes" => entry with { Changes = JsonDocument.Parse("{\"future\":\"" + new string('é', 524288) + "\"}").RootElement.Clone() },
            "key-case" => entry with { CardKey = card.Key.ToLowerInvariant() },
            "key-number" => entry with { CardKey = "VB-ABCDE-01" },
            _ => throw new InvalidOperationException()
        };
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.NotNull(status!.LastError);
        Assert.Equal(1, status.Cursor);
        Assert.Equal("Local", (await store.FindCardAsync(root, card.Id, Ct))!.Title);
        Assert.False(await store.HasLogEntryAsync(entry.Id, Ct));
    }

    [Fact]
    public async Task MissingRemoteCardRequiresRandomKey_AndValidCreationRetainsUnknownFields()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.Web(card, "created", "Created", $$"""{"title":{"to":"Remote"},"lane":{"to":"{{card.ColumnId}}"},"type":{"to":"research-spike"},"priority":{"to":"critical"},"future":[1,2]}""");
        var entry = client.Entries[^1] with { CardId = "card_000000000099", CardKey = "VB-99" };
        client.Entries[^1] = entry;
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        // A legacy wire key the pull admits but a new card may not have: permanent, so passed over.
        Assert.Null(status!.LastError);
        Assert.Equal(2, status.Cursor);
        Assert.Contains("stored random key", Assert.Single(status.SkippedEntries!).Reason);
        Assert.Null(await store.FindCardAsync(root, entry.CardId, Ct));
        Assert.False(await store.HasLogEntryAsync(entry.Id, Ct));
        client.Web(card, "created", "Created", $$"""{"title":{"to":"Remote"},"lane":{"to":"{{card.ColumnId}}"},"type":{"to":"research-spike"},"priority":{"to":"critical"},"future":[1,2]}""");
        entry = client.Entries[^1] with { CardId = "card_000000000098", CardKey = "VB-ABCDE-99" };
        client.Entries[^1] = entry;
        var retried = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(retried!.LastError);
        Assert.Equal(3, retried.Cursor);
        var imported = await store.FindCardAsync(root, entry.CardId, Ct);
        Assert.Equal("Remote", imported!.Title);
        Assert.Equal(card.ColumnId, imported.ColumnId);
        Assert.Equal("research-spike", imported.Type);
        Assert.Equal("critical", imported.Priority);
        Assert.Equal("VB-ABCDE-99", imported.Key);
        Assert.Contains("\"future\":[1,2]", Assert.Single((await store.GetHistoryAsync(root, card.BoardId, imported.Id, 0, Ct))!).Changes);
        Assert.Equal(0, retried.Unsent);
    }

    [Fact]
    public async Task EntriesThatCanNeverApplyArePassedOverAndCounted_WhileLaterEntriesStillApply()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        var sprint = await store.CreateBoardAsync(root, "Sprint", Ct);
        var elsewhere = await store.CreateCardAsync(root, new(null, "Other board", "", null, "medium", null, [], false, BoardId: sprint.Id), Ct);

        // A comment on a card this machine never received.
        client.Web(card, "comment", "On a missing card", null);
        client.Entries[^1] = client.Entries[^1] with { CardId = "card_00000000beef", CardKey = "VB-ZZZZZ-77" };
        // A creation under a key a local card already holds.
        client.Web(card, "created", "Created", $$$"""{"title":{"to":"Clash"},"lane":{"to":"{{{card.ColumnId}}}"}}""");
        client.Entries[^1] = client.Entries[^1] with { CardId = "card_00000000cafe" };
        // A change aimed at a card of another local board.
        client.Web(elsewhere, "change", "Wrong board", """{"title":{"to":"Moved in from the web"}}""");
        client.Web(card, "comment", "Still applies", null);

        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(status!.LastError);
        Assert.Equal(5, status.Cursor);
        Assert.Equal("Still applies", Assert.Single((await store.GetCardDetailAsync(root, card.Id, Ct))!.Comments).Body);
        Assert.Equal("Other board", (await store.FindCardAsync(root, elsewhere.Id, Ct))!.Title);
        Assert.Null(await store.FindCardAsync(root, "card_00000000cafe", Ct));
        Assert.Equal(3, status.Skipped);
        var reasons = status.SkippedEntries!.ToDictionary(e => e.Seq, e => e.Reason);
        Assert.Contains("card this machine does not have", reasons[2]);
        Assert.Contains("conflicts with a local card", reasons[3]);
        Assert.Contains("different local board", reasons[4]);
        Assert.Equal(3, (await service.GetStatusAsync(root, card.BoardId, Ct))!.Skipped);
    }

    [Fact]
    public async Task ANewerVersionAppliesWhatAnEarlierOneSkipped_WithoutUndoingALaterEdit()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.Web(card, "change", "Changed: title, priority", """{"title":{"to":"Older"},"priority":{"to":"high"}}""");
        client.Web(card, "comment", "Missed by the earlier version", null);
        client.Web(card, "change", "Changed: title", """{"title":{"to":"Newer"}}""");
        // An earlier version could not apply the first two web entries: it passed them over and recorded that it tried.
        var unknown = client.Entries.Skip(1).Take(2).Select(e => e.Id).ToHashSet();
        client.TransformPull = page => page with { Entries = page.Entries.Select(e => unknown.Contains(e.Id) ? e with { Kind = "future_kind" } : e).ToList() };
        Assert.Equal(2, (await service.SyncNowAsync(root, card.BoardId, Ct))!.Skipped);
        await ExecuteAsync("UPDATE BoardSyncSkippedEntries SET Version = '1.0.0';");
        client.TransformPull = null;

        var calls = client.PullAfters.Count;
        var retried = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(retried!.LastError);
        Assert.Equal(1, client.PullAfters[calls]);
        Assert.Equal(4, retried.Cursor);
        Assert.Equal(0, retried.Skipped);
        // The replayed change is older than the title edit after it: it sets the priority, not the title.
        var current = (await store.FindCardAsync(root, card.Id, Ct))!;
        Assert.Equal("Newer", current.Title);
        Assert.Equal("high", current.Priority);
        Assert.Equal("Missed by the earlier version", Assert.Single((await store.GetCardDetailAsync(root, card.Id, Ct))!.Comments).Body);

        // Once: the next sync pulls from the cursor again.
        calls = client.PullAfters.Count;
        await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Equal(4, Assert.Single(client.PullAfters.Skip(calls)));
    }

    [Fact]
    public async Task AnEntryThisVersionStillCannotApplyIsTriedOnce_AndANewerVersionsRecordIsLeftAlone()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.Web(card, "restored", "Declared but not applied by this version", null);
        Assert.Equal(1, (await service.SyncNowAsync(root, card.BoardId, Ct))!.Skipped);
        Assert.Equal(VibeRails.VersionInfo.Version, await SkippedVersionAsync());

        // Last tried by an earlier version: tried again, still skipped, and recorded under this version and reason.
        await ExecuteAsync("UPDATE BoardSyncSkippedEntries SET Version = '1.0.0', Reason = 'Earlier wording';");
        var calls = client.PullAfters.Count;
        var retried = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Equal(1, client.PullAfters[calls]);
        Assert.Equal(2, retried!.Cursor);
        Assert.Contains("cannot apply remote entry kind", Assert.Single(retried.SkippedEntries!).Reason);
        Assert.Equal(VibeRails.VersionInfo.Version, await SkippedVersionAsync());
        calls = client.PullAfters.Count;
        await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Equal(2, Assert.Single(client.PullAfters.Skip(calls)));

        // Last tried by a newer version: this one cannot do better, so it neither rewinds nor rewrites the record.
        await ExecuteAsync("UPDATE BoardSyncSkippedEntries SET Version = '999.0.0';");
        calls = client.PullAfters.Count;
        await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Equal(2, Assert.Single(client.PullAfters.Skip(calls)));
        Assert.Equal("999.0.0", await SkippedVersionAsync());
    }

    [Fact]
    public async Task CardsAnOlderBinaryCreatesAfterPublishingGetABaseline_SoTheyAndTheirCommentsAreSent()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        // An older binary writes the card and its comment and no Card Log entry at all.
        await using (var db = new Microsoft.Data.Sqlite.SqliteConnection(cs))
        {
            await db.OpenAsync(Ct);
            var sql = db.CreateCommand();
            sql.CommandText = """
                INSERT INTO BoardCards (Id, ProjectPath, Number, ColumnId, Position, Title, Description, Priority, Tags, Blocked, CreatedUTC, UpdatedUTC)
                SELECT 'card_0123456789ab', ProjectPath, 900, ColumnId, 0, 'Older writer', '', 'medium', '[]', 0, CreatedUTC, UpdatedUTC FROM BoardCards WHERE Id = $id;
                INSERT INTO BoardComments (Id, CardId, AuthorKind, AuthorLabel, Body, CreatedUTC)
                VALUES ('cm_olderwriter', 'card_0123456789ab', 'user', 'You', 'Written by an older binary', strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """;
            sql.Parameters.AddWithValue("$id", card.Id);
            await sql.ExecuteNonQueryAsync(Ct);
        }

        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Null(status!.LastError);
        Assert.Equal(0, status.Unsent);
        var created = Assert.Single(client.Entries, e => e.CardId == "card_0123456789ab" && e.Kind == "created");
        Assert.Equal("Older writer", created.Changes!.Value.GetProperty("title").GetProperty("to").GetString());
        var comment = Assert.Single(client.Entries, e => e.Id == "cm_olderwriter");
        Assert.True(created.Seq < comment.Seq);
        // Nothing left to baseline: the next push only reads.
        Assert.Equal(0, await store.WriteSyncBaselineAsync(root, card.BoardId, Ct));
    }

    [Fact]
    public async Task UnsentPublicationBaselineLocksNoField_WhileAFreshLocalCreationStillDoes()
    {
        var card = await Card();
        await using (var db = new Microsoft.Data.Sqlite.SqliteConnection(cs))
        {
            // A card from before board/14: no created entry until Publish writes the baseline.
            await db.OpenAsync(Ct);
            var sql = db.CreateCommand();
            sql.CommandText = "DELETE FROM BoardComments WHERE CardId = $id;";
            sql.Parameters.AddWithValue("$id", card.Id);
            await sql.ExecuteNonQueryAsync(Ct);
        }
        Assert.Equal(1, await store.WriteSyncBaselineAsync(root, card.BoardId, Ct));
        Assert.Empty(await store.GetFieldsChangedAfterAsync(card.Id, 0, Ct));

        var fresh = await Card();
        Assert.Contains("title", await store.GetFieldsChangedAfterAsync(fresh.Id, 0, Ct));

        // A web edit pulled while the baseline is still queued applies to the card.
        var applied = await store.UpdateSyncedCardAsync(root, card.Id, new(Title: "Colleague's title"), BoardAuthor.User(),
            new("log_web_title", 7, DateTime.UtcNow, "Changed: title", """{"title":{"to":"Colleague's title"}}""", card.BoardId), Ct);
        Assert.Equal("Colleague's title", applied!.Title);
    }

    [Fact]
    public async Task DestinationChangesStopBeforeAnyNetworkCall()
    {
        var card = await Card();
        await service.SetPublishedAsync(root, card.BoardId, true, Ct);
        client.DestinationKey = "other-server-or-account";
        var calls = client.Calls;
        var status = await service.SyncNowAsync(root, card.BoardId, Ct);
        Assert.Contains("server or API key changed", status!.LastError);
        Assert.Equal(calls, client.Calls);
    }

    [Fact]
    public async Task ManualOperationsRespectTheCrossProcessLock()
    {
        var card = await Card();
        using var held = new BoardSyncLock(Path.Combine(root, "sync.lock")).TryAcquire();
        Assert.NotNull(held);
        await Assert.ThrowsAsync<BoardValidationException>(() => service.SetPublishedAsync(root, card.BoardId, true, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => service.SyncNowAsync(root, card.BoardId, Ct));
        await service.SyncDueAsync(Ct);
        Assert.Equal(0, client.Calls);
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<string?> SkippedVersionAsync()
    {
        await using var db = new Microsoft.Data.Sqlite.SqliteConnection(cs);
        await db.OpenAsync(Ct);
        await using var command = db.CreateCommand();
        command.CommandText = "SELECT Version FROM BoardSyncSkippedEntries;";
        return await command.ExecuteScalarAsync(Ct) as string;
    }

    private sealed class FakeClient : IBoardSyncClient
    {
        public bool IsConfigured => true;
        public string? DestinationKey { get; set; } = "server-and-account";
        public int Calls;
        public bool LoseNextAck, BadAck, Reject, RejectUnknownEntry;
        public string? RejectEntryId;
        public BoardSyncClientException? PushError;
        public Func<BoardSyncPushResponse, BoardSyncPushResponse>? TransformAck;
        public Func<BoardSyncPullResponse, BoardSyncPullResponse>? TransformPull;
        public long? AdvertisedLastSeq;
        public Func<Task>? BeforePull;
        public List<long> PullAfters { get; } = [];
        public List<BoardSyncPulledEntryWire> Entries { get; } = [];
        private readonly Guid remoteId = Guid.NewGuid();
        public Task<BoardSyncPublishResponse> PublishAsync(BoardSyncPublishRequest request, CancellationToken ct, string? expectedDestination = null)
        {
            Calls++;
            return Task.FromResult(new BoardSyncPublishResponse(remoteId, request.Name, Entries.Count));
        }
        public Task<BoardSyncPushResponse> PushAsync(string boardId, BoardSyncPushRequest request, CancellationToken ct, string? expectedDestination = null)
        {
            Calls++;
            if (PushError is { } pushError) throw pushError;
            if (Reject) throw new BoardSyncClientException("Rejected", "invalid_entry");
            if (RejectUnknownEntry) throw new BoardSyncClientException("Rejected", "invalid_entry", 400, "log_other_board");
            if (RejectEntryId is { } rejected && request.Entries.Any(e => e.Id == rejected))
                throw new BoardSyncClientException("Rejected", "invalid_entry", 400, rejected);
            var accepted = new List<BoardSyncAcceptedWire>();
            foreach (var entry in request.Entries)
            {
                var stored = Entries.FirstOrDefault(e => e.Id == entry.Id);
                if (stored is null)
                {
                    stored = new(entry.Id, entry.CardId, entry.CardKey, entry.Kind, entry.Author, entry.Body, entry.Changes, entry.CreatedUtc, Entries.Count + 1, "desktop");
                    Entries.Add(stored);
                }
                accepted.Add(new(entry.Id, stored.Seq));
            }
            if (LoseNextAck) { LoseNextAck = false; throw new BoardSyncClientException("Connection lost", "network"); }
            var response = new BoardSyncPushResponse(BadAck ? [new("unexpected", 5)] : accepted, Entries.Count);
            return Task.FromResult(TransformAck?.Invoke(response) ?? response);
        }
        public async Task<BoardSyncPullResponse> PullAsync(string boardId, long after, int limit, CancellationToken ct, string? expectedDestination = null)
        {
            Calls++;
            PullAfters.Add(after);
            if (BeforePull is { } action) { BeforePull = null; await action(); }
            var response = new BoardSyncPullResponse(Entries.Where(e => e.Seq > after).Take(limit).ToList(), AdvertisedLastSeq ?? Entries.Count, Entries.Count - after > limit);
            return TransformPull?.Invoke(response) ?? response;
        }
        public void Web(BoardCardRecord card, string kind, string body, string? changes) => Entries.Add(new(
            "log_" + Guid.NewGuid().ToString("N"), card.Id, card.Key, kind, new("user", "Web", null), body,
            changes is null ? null : JsonDocument.Parse(changes).RootElement.Clone(), DateTime.UtcNow, Entries.Count + 1, "web"));
    }

    public void Dispose() => Directory.Delete(root, true);
}
