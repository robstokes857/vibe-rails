using Microsoft.Data.Sqlite;
using Moq;
using Tests.VcaRegression;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.ReviewRequests;
using Xunit;

namespace Tests.Services.ReviewRequests;

/// <summary>Tool retries share an open unsent tree across connections without changing existing bundles.</summary>
public sealed class ReviewRequestReuseTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vb-review-reuse-" + Guid.NewGuid().ToString("N"));
    private readonly string _connection;
    private readonly ReviewRequestStore _store;
    private readonly string _project;
    private static readonly string Tree = new('b', 40);
    private static readonly string Base = new('a', 40);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ReviewRequestReuseTests()
    {
        Directory.CreateDirectory(_directory);
        _project = Path.Combine(_directory, "project");
        _connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "state.db"), Pooling = false }.ToString();
        _store = new ReviewRequestStore(_connection);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private ReviewRequestRecord Record() => new(Guid.NewGuid().ToString("D"), _project, "staged", Tree,
        Base, "ORIGINAL", "open", "original bundle", DateTime.UtcNow, DateTime.UtcNow);

    [Fact]
    public async Task ConcurrentConnectionsKeepOneBundleForTheSameProjectAndTree()
    {
        var stores = Enumerable.Range(0, 12).Select(_ => new ReviewRequestStore(_connection)).ToArray();
        var candidates = stores.Select(_ => Record()).ToArray();
        var saved = await Task.WhenAll(stores.Select((store, i) => Task.Run(
            () => store.AddOrGetOpenUnsentAsync(candidates[i], Ct), Ct)));
        Assert.Single(saved.Select(row => row.Id).Distinct());
        var stored = Assert.Single(await _store.ListAsync(_project, 100, Ct));
        Assert.Equal(saved[0], stored);
        Assert.Contains(candidates, row => row.Id == stored.Id);
    }

    [Fact]
    public async Task LookupIsNotLimitedByTheRequestListPage()
    {
        var first = Record() with { CreatedUTC = DateTime.UtcNow.AddDays(-1) };
        await _store.AddAsync(first, Ct);
        for (var i = 0; i < ReviewRequestService.ListLimit + 1; i++)
            await _store.AddAsync(Record() with { TreeId = new string('c', 40) }, Ct);
        Assert.Equal(first, await _store.FindOpenUnsentAsync(_project, Tree, Base, Ct));
    }

    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    [InlineData("cancelled")]
    [InlineData("sent")]
    [InlineData("attempted")]
    [InlineData("other-project")]
    [InlineData("other-tree")]
    [InlineData("other-scope")]
    [InlineData("other-base")]
    [InlineData("unborn-base")]
    public async Task OnlyOpenUnsentStagedRequestsForThisProjectTreeAndBaseAreReused(string variant)
    {
        var original = Record();
        original = variant switch
        {
            "other-project" => original with { ProjectPath = _project + "-other" },
            "other-tree" => original with { TreeId = new string('c', 40) },
            "other-scope" => original with { Scope = "working" },
            "other-base" => original with { BaseCommit = new string('d', 40) },
            "unborn-base" => original with { BaseCommit = null },
            "approved" or "rejected" or "cancelled" => original with { Status = variant },
            _ => original
        };
        await _store.AddAsync(original, Ct);
        if (variant == "sent")
            await _store.MarkSentAsync(original.Id, "rvw_" + new string('a', 32), "key", DateTime.UtcNow, DateTime.UtcNow.AddMonths(1), Ct);
        if (variant == "attempted")
            await _store.MarkSendAttemptAsync(original.Id, "key", DateTime.UtcNow, Ct);
        Assert.Null(await _store.FindOpenUnsentAsync(_project, Tree, Base, Ct));
        var candidate = Record();
        Assert.Equal(candidate, await _store.AddOrGetOpenUnsentAsync(candidate, Ct));
    }

    [Fact]
    public async Task ToolReuseReturnsOnlyIdentityWithoutReadingOrReplacingTheExistingBundle()
    {
        var original = Record();
        await _store.AddAsync(original, Ct);
        var snapshots = new Mock<IGitStagedSnapshotProvider>();
        snapshots.Setup(s => s.CaptureAsync(_project, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStagedSnapshot(
            _project, [new GitStagedFileSnapshot("file.cs", Path.Combine(_project, "file.cs"),
                GitStagedChangeKind.Modified, true, false, 1, "after", PreviousContent: "before")], [],
            StagedIdentity: new GitStagedSnapshotIdentity(new string('a', 40), new string('c', 40), Tree)));
        var service = new ReviewRequestService(_store, snapshots.Object, FakeCommitSignatureVerifier.ValidFor("owner@example.com"));
        var result = await service.CreateForToolAsync(_project, new CreateReviewRequest(Reviewers: ["owner@example.com"], Note: "new"),
            new Mock<ISessionStore>(MockBehavior.Strict).Object, Ct);
        Assert.Null(result.Created);
        Assert.Equal(new ReviewRequestReuse(original.Id, Tree), result.Reused);
        Assert.Equal(original, await _store.GetAsync(original.Id, Ct));
    }

    [Fact]
    public async Task TheSameTreeOnAnotherHeadBuildsANewBundleInsteadOfReusingTheOldDiff()
    {
        // Tree T was requested on HEAD B1; after git reset --soft HEAD~1 the index is still T but
        // the staged diff is B0 to T. Reusing the B1 bundle would let a reviewer sign T without
        // seeing the B0 to B1 changes the commit now carries.
        var original = Record();
        await _store.AddAsync(original, Ct);
        var resetBase = new string('d', 40);
        var snapshots = new Mock<IGitStagedSnapshotProvider>();
        snapshots.Setup(s => s.CaptureAsync(_project, It.IsAny<CancellationToken>())).ReturnsAsync(new GitStagedSnapshot(
            _project, [new GitStagedFileSnapshot("file.cs", Path.Combine(_project, "file.cs"),
                GitStagedChangeKind.Modified, true, false, 1, "after", PreviousContent: "before")], [],
            StagedIdentity: new GitStagedSnapshotIdentity(resetBase, new string('c', 40), Tree)));
        var service = new ReviewRequestService(_store, snapshots.Object, FakeCommitSignatureVerifier.ValidFor("owner@example.com"));
        var sessions = new Mock<ISessionStore>(MockBehavior.Strict);
        var result = await service.CreateForToolAsync(_project, new CreateReviewRequest(Reviewers: ["owner@example.com"]),
            sessions.Object, Ct);
        Assert.Null(result.Reused);
        var created = Assert.IsType<ReviewRequestSummary>(result.Created);
        Assert.NotEqual(original.Id, created.Id);
        Assert.Equal(resetBase, (await _store.GetAsync(created.Id, Ct))!.BaseCommit);
        Assert.Equal(original, await _store.GetAsync(original.Id, Ct));
    }
}
