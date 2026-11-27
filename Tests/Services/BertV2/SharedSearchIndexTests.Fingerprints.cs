using VibeRails.Data.Sqlite;
using Xunit;

namespace Tests.Services.BertV2;

/// <summary>
/// VIBE-327: a scheduled sweep skips a corpus whose sources have not changed since its last completed
/// sweep, the Board sweep stops re-reading every commit snapshot, and history pages read their file
/// changes in one query.
/// </summary>
public sealed partial class SharedSearchIndexTests
{
    private static readonly TimeSpan Scheduled = TimeSpan.FromMinutes(5);

    /// <summary>Lets the scheduled interval pass for every corpus, as five idle minutes would.</summary>
    private void ElapseInterval() => Execute(SearchPath, """
        UPDATE SearchCheckpoints SET CompletedUtc='2000-01-01T00:00:00.0000000Z';
        UPDATE SearchSourceFingerprints SET CheckedUtc=NULL;
        """);

    private async Task<int> ScheduledSweep() => (await index.ReconcileAsync(board, 25, Ct, minimumInterval: Scheduled)).Documents;

    [Fact]
    public async Task ScheduledSweepSkipsUnchangedCorporaAndSweepsOnlyTheOneThatChanged()
    {
        await SeedSweepCorpora();
        SeedInput("running", 100, "running session input");
        Assert.Equal(22, await ScheduledSweep());
        Execute(SearchPath, """
            CREATE TABLE PresenceWrites(Id TEXT);
            CREATE TRIGGER AuditPresence AFTER UPDATE ON SearchDocumentPresence BEGIN
                INSERT INTO PresenceWrites VALUES(new.DocumentId); END;
            """);

        ElapseInterval();
        Assert.Equal(0, await ScheduledSweep());
        Assert.Equal(0L, Scalar("SELECT count(*) FROM PresenceWrites"));
        Assert.Equal(3L, Scalar("SELECT count(*) FROM SearchSourceFingerprints WHERE CheckedUtc IS NOT NULL"));
        // A checked corpus waits out the interval again before it compares another fingerprint.
        Assert.Equal(0, await ScheduledSweep());

        var card = OrderedDocuments("board")[0];
        Execute(Path.Combine(root, "board.db"), "UPDATE BoardCards SET Title='Renamed card' WHERE Id=$id", ("$id", card.Id));
        ElapseInterval();
        Assert.Equal(7, await ScheduledSweep());
        Assert.Contains(index.ReadDocuments("board"), d => d.Board!.Title == "Renamed card");

        // A prompt in a running session changes the inputs, not any ended session's document.
        SeedInput("running", 101, "another running input");
        ElapseInterval();
        Assert.Equal(9, await ScheduledSweep());
        Assert.Equal(9, index.Count("input"));

        Execute(State, "UPDATE Sessions SET EndedUTC=$utc WHERE Id='running'", ("$utc", DateTime.UtcNow.ToString("O")));
        ElapseInterval();
        Assert.Equal(8, await ScheduledSweep());
        Assert.Equal(8, index.Count("session"));

        await DeleteSource(OrderedDocuments("input")[0]);
        ElapseInterval();
        Assert.Equal(16, await ScheduledSweep()); // the deletion changes both history corpora
        Assert.Equal(8, index.Count("input"));
    }

    [Theory]
    [InlineData("board")]
    [InlineData("input")]
    [InlineData("session")]
    public async Task UnchangedCorpusIsSweptAgainAfterAnOlderProcessSweepsIt(string kind)
    {
        await SeedSweepCorpora();
        await ScheduledSweep();
        // An older process records no fingerprint; its completion moves the epoch past ours.
        LegacyPage(kind, OrderedDocuments(kind), finished: true);
        ElapseInterval();
        Assert.Equal(7, await ScheduledSweep());
        ElapseInterval();
        Assert.Equal(0, await ScheduledSweep());
    }

    [Fact]
    public async Task UnchangedCorpusIsSweptAgainOnceTheBackstopPasses()
    {
        await SeedSweepCorpora();
        await ScheduledSweep();
        Execute(SearchPath, "UPDATE SearchSourceFingerprints SET FullSweepUtc=$old WHERE Kind='input'",
            ("$old", DateTime.UtcNow.Subtract(SqliteSearchIndexStore.FullSweepBackstop).AddMinutes(-1).ToString("O")));
        ElapseInterval();
        Assert.Equal(7, await ScheduledSweep());
        Assert.Equal(1L, Scalar("SELECT count(*) FROM SearchSourceFingerprints WHERE Kind='input' AND FullSweepUtc>$recent",
            ("$recent", DateTime.UtcNow.AddMinutes(-1).ToString("O"))));
    }

    [Fact]
    public async Task InterruptedSweepRecordsTheFingerprintItStartedFromNotALaterOne()
    {
        await SeedSweepCorpora();
        await ScheduledSweep();
        Execute(Path.Combine(root, "board.db"), "UPDATE BoardCards SET Title='Before the pause'");
        ElapseInterval();
        // One page, then the sweep yields; a card changes before the next call resumes it.
        await index.ReconcileAsync(board, 2, Ct, TimeSpan.Zero, Scheduled);
        var started = (string)Scalar("SELECT PendingFingerprint FROM SearchSourceFingerprints WHERE Kind='board'")!;
        var first = OrderedDocuments("board")[0];
        Execute(Path.Combine(root, "board.db"), "UPDATE BoardCards SET Title='Changed during the sweep' WHERE Id=$id", ("$id", first.Id));
        await index.ReconcileAsync(board, 25, Ct, minimumInterval: Scheduled);
        Assert.Equal(started, Scalar("SELECT Fingerprint FROM SearchSourceFingerprints WHERE Kind='board'"));
        // The page read before the change is caught by the next scheduled sweep.
        ElapseInterval();
        Assert.Equal(7, await ScheduledSweep());
        Assert.Contains(index.ReadDocuments("board"), d => d.Board!.Title == "Changed during the sweep");
    }

    [Fact]
    public async Task BoardSweepReusesFileCandidatesUntilTheCardsSnapshotsChange()
    {
        var linked = await Card("Linked card");
        var plain = await Card("Plain card");
        LinkCommit(linked.Id, "aaaa", "2026-11-01T00:00:00.0000000Z", "first.cs");
        await index.ReconcileAsync(board, 25, Ct);
        Assert.Equal(["first.cs"], Candidates(linked.Id));
        Assert.Empty(Candidates(plain.Id));
        Assert.Null(index.ReadDocuments("board").Single(d => d.Id == plain.Id).Board!.CandidateKey);

        // Snapshots are insert-only, so the sweep must not parse one again. Rewriting it here, which
        // the product never does, shows the indexed candidates were reused.
        Execute(Path.Combine(root, "board.db"), "UPDATE BoardCommitSnapshots SET SnapshotJson=$json",
            ("$json", Snapshot("rewritten.cs")));
        await index.ReconcileAsync(board, 25, Ct);
        Assert.Equal(["first.cs"], Candidates(linked.Id));

        LinkCommit(linked.Id, "bbbb", "2026-11-02T00:00:00.0000000Z", "second.cs");
        await index.ReconcileAsync(board, 25, Ct);
        Assert.Equal(["second.cs", "rewritten.cs"], Candidates(linked.Id));
        Execute(Path.Combine(root, "board.db"), "DELETE FROM BoardCommits WHERE Sha='bbbb'");
        await index.ReconcileAsync(board, 25, Ct);
        Assert.Equal(["rewritten.cs"], Candidates(linked.Id));
    }

    [Fact]
    public async Task HistoryPagesMergeEachDocumentsFileChangesInInputOrder()
    {
        SeedInput("merged", 1, "first input", ended: true);
        SeedInput("merged", 2, "second input", ended: true);
        SeedInput("other", 3, "other input", ended: true);
        Execute(State, """
            INSERT INTO InputFileChanges(UserInputId,FilePath,ChangeType,LinesAdded,LinesDeleted) VALUES(1,'second.cs','added',5,NULL);
            INSERT INTO InputFileChanges(UserInputId,FilePath,ChangeType,LinesAdded,LinesDeleted) VALUES(2,'file.cs','deleted',NULL,4);
            """);
        await index.ReconcileAsync(board, 25, Ct);
        var session = index.ReadDocuments("session").Single(d => d.Session!.SessionId == "merged");
        Assert.Equal(["file.cs:deleted:2:4", "second.cs:added:5:0"],
            session.FileChanges!.Select(f => $"{f.FilePath}:{f.ChangeType}:{f.LinesAdded}:{f.LinesDeleted}"));
        Assert.Equal(2, session.Session!.InputCount);
        Assert.Equal("first input\n\nsecond input", session.Sources.Single().Text);
        var first = index.ReadDocuments("input").Single(d => d.Input!.UserInputId == 1);
        Assert.Equal(["file.cs", "second.cs"], first.FileChanges!.Select(f => f.FilePath));
        Assert.Equal(2, first.Input!.FileChangeCount);
        Assert.Equal(1, index.ReadDocuments("input").Single(d => d.Input!.UserInputId == 3).Input!.FileChangeCount);
    }

    private IReadOnlyList<string> Candidates(string cardId) =>
        index.ReadDocuments("board").Single(d => d.Id == cardId).Board!.FileCandidates.Select(f => f.Path).ToArray();

    private static string Snapshot(string file) => $$"""{"files":[{"fileName":"{{file}}"}]}""";

    private void LinkCommit(string cardId, string sha, string linked, string file) => Execute(Path.Combine(root, "board.db"), """
        INSERT INTO BoardCommits(CardId,Sha,Author,Message,CommittedUTC,LinkedUTC) VALUES($card,$sha,'author','message',$linked,$linked);
        INSERT INTO BoardCommitSnapshots(CardId,Sha,SnapshotJson) VALUES($card,$sha,$json);
        """, ("$card", cardId), ("$sha", sha), ("$linked", linked), ("$json", Snapshot(file)));
}
