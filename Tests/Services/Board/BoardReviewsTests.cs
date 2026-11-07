using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Git;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardReviewsTests : IAsyncLifetime
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-reviews-" + Guid.NewGuid().ToString("N"));
    private string repo = null!, state = null!, boardPath = null!, baseline = null!;
    private Repository repository = null!;
    private BoardStore store = null!;
    private JobStore jobs = null!;
    private BoardReviewService reviews = null!;
    private BoardService service = null!;
    private BoardCardRecord card = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        repo = Path.Combine(root, "checkout"); Directory.CreateDirectory(repo);
        await Git("init"); await Git("config", "user.email", "tests@local"); await Git("config", "user.name", "Reviews");
        await File.WriteAllTextAsync(Path.Combine(repo, "sample.cs"), "class Before {}\n", Ct);
        await Git("add", "."); await Git("-c", "core.hooksPath=", "commit", "-m", "baseline");
        baseline = await Git("rev-parse", "HEAD");
        state = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
        boardPath = $"Data Source={Path.Combine(root, "board.db")};Pooling=False";
        repository = new Repository(state);
        store = new BoardStore(boardPath, state);
        jobs = new JobStore(state, store);
        service = new(store, Mock.Of<IBoardCommitService>(), new NullBoardLiveSessionProbe());
        reviews = new(store, service);
        await store.EnsureDefaultColumnsAsync(repo, Ct);
        card = await store.CreateCardAsync(repo, new(null, "Review", "", null, "medium", null, [], false), Ct);
    }





    [Fact]
    public async Task CanonicalReportSurvivesRestart_RequiresOwningSession_AndDetectsDirtyChanges()
    {
        var session = await DirectReview();
        await File.WriteAllTextAsync(Path.Combine(repo, "sample.cs"), "class After {}\n", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "new.txt"), "untracked\n", Ct);
        var begun = await reviews.BeginAsync(repo, card.Id, session, repo, "working-tree", "The two dirty files", null, null, true, Ct);
        Assert.NotNull(begun.SnapshotHash); Assert.Equal(baseline, begun.HeadCommit);
        Assert.False(await store.SaveReviewAsync(repo, begun with { CapturedUtc = begun.CapturedUtc!.Value.AddSeconds(1), ScopeDescription = "Concurrent replacement" }, Ct));
        await Assert.ThrowsAsync<BoardValidationException>(() => reviews.SaveAsync(repo, card.Id, "other", begun.Id,
            "No findings reported", "None", "Read both files", "No tests", Ct));
        var saved = await reviews.SaveAsync(repo, card.Id, session, begun.Id, "Findings", "sample.cs:1 — explain the regression", "Inspected files", "No runtime tests", Ct);
        Assert.Equal("Findings", saved.Result);
        Assert.Equal("Findings", (await reviews.SaveAsync(repo, card.Id, session, begun.Id, "No findings reported", "None", "None", "None", Ct)).Result);
        var reopened = new BoardStore(boardPath, state);
        Assert.Equivalent(saved, await reopened.GetReviewAsync(repo, card.Id, begun.Id, Ct));
        Assert.Equal("code_review", BoardCommentPurpose.Read(Assert.Single((await reopened.GetCardDetailAsync(repo, card.Id, Ct))!.Comments).Changes));
        Assert.Empty(await reopened.GetReviewsAsync(Path.Combine(root, "foreign"), card.Id, 0, Ct));
        Assert.StartsWith("Current", (await reviews.ReportAsync(repo, card.Key, begun.Id, true, repo, Ct))!.Freshness);
        await File.WriteAllTextAsync(Path.Combine(repo, "new.txt"), "changed after review\n", Ct);
        Assert.StartsWith("Stale", (await reviews.ReportAsync(repo, card.Key, begun.Id, true, repo, Ct))!.Freshness);
        Assert.StartsWith("Unknown", (await reviews.ReportAsync(repo, card.Key, begun.Id, true, root, Ct))!.Freshness);
        Assert.Equal(saved.Findings, (await reopened.GetReviewAsync(repo, card.Id, begun.Id, Ct))!.Findings);
    }







    [Fact]
    public async Task RangePlusDirtyScopeIncludesBothInputs_AndUnrelatedCheckoutIsUnknown()
    {
        await Git("config", "core.fsmonitor", "true");
        await File.WriteAllTextAsync(Path.Combine(repo, "sample.cs"), "class Committed {}\n", Ct);
        await Git("add", "."); await Git("-c", "core.hooksPath=", "commit", "-m", "change");
        var head = await Git("rev-parse", "HEAD");
        await File.WriteAllTextAsync(Path.Combine(repo, "dirty.txt"), "one\n", Ct);
        var session = await DirectReview();
        var begun = await reviews.BeginAsync(repo, card.Id, session, repo, "range", "Committed change plus dirty file", baseline, head, true, Ct);
        Assert.NotNull(begun.SnapshotHash); Assert.Equal(baseline, begun.BaseCommit); Assert.Equal(head, begun.HeadCommit);
        await reviews.SaveAsync(repo, card.Id, session, begun.Id, "No findings reported", "None", "Read changed files", "No tests", Ct);
        Assert.StartsWith("Current", (await reviews.ReportAsync(repo, card.Id, begun.Id, true, repo, Ct))!.Freshness);
        await File.WriteAllTextAsync(Path.Combine(repo, "dirty.txt"), "two\n", Ct);
        Assert.StartsWith("Stale", (await reviews.ReportAsync(repo, card.Id, begun.Id, true, repo, Ct))!.Freshness);
    }

    [Fact]
    public async Task FailedRangeCaptureReportsStage_AndKeepsFreshnessUnknown()
    {
        var session = await DirectReview();
        var begun = await reviews.BeginAsync(repo, card.Id, session, repo, "range", "Unavailable base",
            new string('0', 40), baseline, true, Ct);

        Assert.Null(begun.SnapshotHash);
        Assert.Contains("Scope capture unavailable", begun.CaptureLimitations);
        Assert.Contains("capture range changes", begun.CaptureLimitations);
        // The provider's own failure text rides inside the limitation, so the agent that reads the
        // record learns the cause without access to the application log.
        Assert.Contains("ancestor of the selected head", begun.CaptureLimitations);
        Assert.Contains("not a finding about the change", begun.CaptureLimitations);
        Assert.DoesNotContain('\n', begun.CaptureLimitations!);
        await reviews.SaveAsync(repo, card.Id, session, begun.Id, "Incomplete", "None", "Capture failed", "Scope unavailable", Ct);
        Assert.StartsWith("Unknown", (await reviews.ReportAsync(repo, card.Id, begun.Id, true, repo, Ct))!.Freshness);
    }



    [Fact]
    public async Task OlderWritersOmittingPurposeKeepWorkDefaults_WithoutOverwritingNewPurpose()
    {
        var env = await repository.SaveEnvironmentAsync(new() { CustomName = "Reviewer", LLM = LLM.Codex, Purpose = "code_review" }, Ct);
        var job = await jobs.CreateJobAsync(new("Review", repo, LLM.Codex, env.Id, "Review", null, true, []), Ct);
        var run = (await jobs.EnqueueBoardCardRunAsync(repo, job.Id, card.Key, Ct))!;
        await using var connection = new SqliteConnection(state); await connection.OpenAsync(Ct);
        await using var old = connection.CreateCommand();
        old.CommandText = """
            UPDATE Environments SET CustomName = 'Renamed' WHERE Id = $env;
            INSERT INTO Environments (CustomName, LLM, CreatedUTC, LastUsedUTC)
                VALUES ('Old writer review', 2, $now, $now);
            INSERT INTO JobRuns (Id, JobId, TriggerKind, TriggerKey, Status, JobName, ProjectPath, Llm,
                EnvironmentId, EnvironmentName, TimeoutMinutes, QueuedUTC)
                SELECT 'old-run', JobId, TriggerKind, 'old-trigger', Status, JobName, ProjectPath, Llm,
                    EnvironmentId, EnvironmentName, TimeoutMinutes, QueuedUTC FROM JobRuns WHERE Id = $run;
            """;
        old.Parameters.AddWithValue("$env", env.Id); old.Parameters.AddWithValue("$run", run); old.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        await old.ExecuteNonQueryAsync(Ct);
        Assert.Equal("code_review", (await repository.GetEnvironmentByIdAsync(env.Id, Ct))!.Purpose);
        Assert.Equal("work", (await jobs.GetRunAsync("old-run", Ct))!.Purpose);
        Assert.Equal("code_review", (await jobs.GetRunAsync(run, Ct))!.Purpose);
        await using var read = connection.CreateCommand(); read.CommandText = "SELECT Purpose FROM Environments WHERE CustomName = 'Old writer review';";
        Assert.Equal("work", await read.ExecuteScalarAsync(Ct));
    }

    private async Task<string> DirectReview()
    {
        var session = Guid.NewGuid().ToString();
        await store.LinkSessionAsync(repo, card.Id, session, "tab", "base:codex", "codex", "Reviewer", "code_review", Ct);
        await store.SaveReviewAsync(repo, new("review_" + Guid.NewGuid().ToString("N"), card.Id, "codex", "Reviewer", DateTime.UtcNow,
            SessionId: session, TabId: "tab", ProcessStatus: "Running"), Ct);
        return session;
    }

    private async Task<string> Git(params string[] args)
    {
        var result = await GitCli.RunAsync(repo, args, Ct); Assert.True(result.Succeeded, result.StdErr); return result.StdOut.Trim();
    }

    public ValueTask DisposeAsync()
    {
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
        return ValueTask.CompletedTask;
    }

}
