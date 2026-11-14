using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.Services.ReviewRequests;
using Xunit;

namespace Tests.Services.ReviewRequests;

/// <summary>
/// VIBE-322: every card editor lists the project's review requests. The list reads the reviewers and
/// counts inside SQLite instead of loading each bundle, and all decisions in one query; it must say
/// exactly what the full read says.
/// </summary>
public sealed class ReviewRequestListTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vb-review-list-" + Guid.NewGuid().ToString("N"));
    private readonly ReviewRequestStore _store;
    private const string Project = "C:/repo";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ReviewRequestListTests()
    {
        Directory.CreateDirectory(_directory);
        _store = new ReviewRequestStore(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(_directory, "state.db"), Pooling = false }.ToString());
    }

    [Fact]
    public async Task SummariesMatchTheFullBundleAndBatchedDecisionsMatchSingleReads()
    {
        var created = DateTime.UtcNow.AddHours(-1);
        for (var i = 0; i < 5; i++)
        {
            var bundle = new ReviewBundle(1, "repo", "staged", $"tree{i}", "base", null,
                Enumerable.Range(0, i).Select(n => new ReviewBundleFile($"f{n}.cs", "modified", false, null, 1, "", new string('x', 10_000), null)).ToList(),
                Enumerable.Range(0, i % 3).Select(n => new ReviewBundleSession($"s{n}", "Session", "codex", created, null)).ToList(),
                Enumerable.Range(0, i % 2 + 1).Select(n => $"reviewer{n}@example.com").ToList(), null, null, created);
            await _store.AddAsync(new ReviewRequestRecord($"req{i}", Project, "staged", $"tree{i}", null, null, ReviewRequestStatus.Open,
                JsonSerializer.Serialize(bundle, ReviewRequestJsonContext.Default.ReviewBundle), created.AddMinutes(i), created.AddMinutes(i)), Ct);
            for (var d = 0; d < i % 3; d++)
                await _store.RecordDecisionAsync(new ReviewDecisionRecord(0, $"req{i}", "reviewer0@example.com", "rejected", $"no {d}",
                    null, null, null, null, created.AddMinutes(i + d), null), created, Ct);
        }

        var full = await _store.ListAsync(Project, 100, Ct);
        var listed = await _store.ListSummariesAsync(Project, 100, Ct);
        Assert.Equal(full.Select(r => r.Id), listed.Select(r => r.Request.Id));
        var batched = await _store.GetDecisionsAsync(full.Select(r => r.Id).ToList(), Ct);
        foreach (var (record, item) in full.Zip(listed))
        {
            var bundle = ReviewRequestService.Bundle(record);
            Assert.Equal(bundle.Reviewers, item.Reviewers);
            Assert.Equal(bundle.Files.Count, item.FileCount);
            Assert.Equal(bundle.Sessions.Count, item.SessionCount);
            Assert.Equal("", item.Request.BundleJson);
            Assert.Equal(record with { BundleJson = "" }, item.Request);
            var single = await _store.GetDecisionsAsync(record.Id, Ct);
            Assert.Equal(single, batched.TryGetValue(record.Id, out var decisions) ? decisions : []);
        }
        Assert.Empty(await _store.GetDecisionsAsync(Array.Empty<string>(), Ct));
        Assert.Equal(2, (await _store.ListSummariesAsync(Project, 2, Ct)).Count);
    }

    [Fact]
    public async Task ACorruptBundleIsReportedAndAMissingReviewerListReadsAsEmpty()
    {
        await _store.AddAsync(new ReviewRequestRecord("missing", Project, "staged", "tree", null, null, ReviewRequestStatus.Open,
            """{"files":[{}],"sessions":[]}""", DateTime.UtcNow, DateTime.UtcNow), Ct);
        var item = Assert.Single(await _store.ListSummariesAsync(Project, 100, Ct));
        Assert.Empty(item.Reviewers!);
        Assert.Equal(1, item.FileCount);
        await _store.AddAsync(new ReviewRequestRecord("corrupt", Project, "staged", "tree2", null, null, ReviewRequestStatus.Open,
            "{not json", DateTime.UtcNow.AddMinutes(1), DateTime.UtcNow.AddMinutes(1)), Ct);
        var corrupt = (await _store.ListSummariesAsync(Project, 100, Ct)).Single(r => r.Request.Id == "corrupt");
        Assert.Null(corrupt.Reviewers);
        Assert.Equal(0, corrupt.FileCount);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }
}
