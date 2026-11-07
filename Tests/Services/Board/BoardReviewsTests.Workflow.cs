using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.Launches;
using VibeRails.Services.Board;
using VibeRails.Services.Jobs;
using VibeRails.Services.Launches;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardReviewsTests
{
    private sealed record Flow(LaunchStore Runs, LaunchService Launches, BoardLaunchWorkflow Workflow,
        LaunchRun Run, LaunchClaim Claim, string Session, string Entry, string Lane);

    private async Task<Flow> NewWorkflow()
    {
        var runs = new LaunchStore($"Data Source={Path.Combine(root, "launches.db")};Pooling=False");
        var launches = new LaunchService(runs, Mock.Of<IAutomationScriptService>(), store);
        reviews = new BoardReviewService(store, service, runs);
        var workflow = new BoardLaunchWorkflow(store, runs, launches, reviews);
        var first = (await launches.SavePresetAsync(null, new("Reviewer", "", "codex", InitialPrompt: "Review", Purpose: "code_review"), Ct)).Preset;
        var next = (await launches.SavePresetAsync(null, new("Next", "", "codex", InitialPrompt: "Work"), Ct)).Preset;
        var lane = (await store.GetColumnsAsync(repo, Ct)).Single(c => c.Name == "Review").Id;
        await workflow.SaveAsync(repo, lane, new([new(first.Id, "preset", "review"), new(next.Id, "preset", "result")], 0), Ct);
        await store.MoveCardAsync(repo, card.Id, lane, null, Ct);
        await using (var db = new SqliteConnection(boardPath))
        {
            await db.OpenAsync(Ct);
            await using var command = db.CreateCommand();
            command.CommandText = "UPDATE BoardLaunchEvents SET DueUnixMs=1";
            await command.ExecuteNonQueryAsync(Ct);
        }
        await workflow.TickAsync(Ct);
        var run = Assert.Single(await runs.GetActiveRunsAsync(Ct));
        var claim = (await runs.TryClaimAsync(run.Id, 3, Environment.ProcessId, DateTime.UtcNow, Ct))!;
        var session = Guid.NewGuid().ToString();
        await runs.StartAsync(claim, session, "Review", repo, Environment.ProcessId, DateTime.UtcNow, ct: Ct);
        await repository.CreateSessionAsync(session, "codex", null, repo, Environment.ProcessId);
        await store.LinkSessionAsync(repo, card.Id, session, null, $"preset:{first.Id}:codex", "codex", "Reviewer", "code_review", Ct);
        return new(runs, launches, workflow, run, claim, session, run.Snapshot.Context.LaneEntryId!, lane);
    }

    private async Task<string> SavePass(Flow f)
    {
        var report = await reviews.BeginAsync(repo, card.Id, f.Session, repo, "repository", "All code", null, null, true, Ct);
        await reviews.SaveAsync(repo, card.Id, f.Session, report.Id, "Findings", "sample.cs:1 Low: optional naming change", "Read code", "None", Ct);
        await f.Workflow.TryReportAsync(repo, card.Id, repo, BoardAuthor.Agent("Codex", "codex", f.Session), "passed", "No blockers", f.Entry, report.Id, Ct);
        return report.Id;
    }

    [Fact]
    public async Task WorkflowReviewRequiresExplicitPassAndSuccessfulProcessCompletion()
    {
        var f = await NewWorkflow();
        var report = await SavePass(f);
        await f.Workflow.TickAsync(Ct);
        Assert.Single(await f.Runs.GetActiveRunsAsync(Ct));
        await f.Runs.CompleteAsync(f.Claim, "succeeded", 0, ct: Ct);
        await f.Workflow.TickAsync(Ct);
        var next = Assert.Single(await f.Runs.GetActiveRunsAsync(Ct));
        Assert.NotEqual(f.Run.Id, next.Id);
        Assert.Equal(report, (await f.Runs.GetRunAsync(f.Run.Id, Ct))!.EvidenceId);
    }

    [Theory]
    [InlineData("succeeded", false)]
    [InlineData("failed", true)]
    public async Task ProcessSuccessWithoutVerdictAndPassWithFailedProcessBothBlock(string outcome, bool report)
    {
        var f = await NewWorkflow();
        if (report) await SavePass(f);
        await f.Runs.CompleteAsync(f.Claim, outcome, outcome == "succeeded" ? 0 : 1, ct: Ct);
        await f.Workflow.TickAsync(Ct);
        Assert.Empty(await f.Runs.GetActiveRunsAsync(Ct));
        var retry = await f.Launches.RetryAsync(repo, f.Run.Id, Ct);
        Assert.Equal(f.Run.Id, retry.SourceRunId);
        var entry = (await store.GetLaunchEventsAsync(repo, card.Id, Ct)).Single(e => e.Id == f.Entry);
        Assert.Equal(retry.Id, entry.RunId);
        Assert.Null(entry.Verdict);
        await store.ReportLaunchEventAsync(repo, card.Id, f.Entry, "skipped", "User skip", null, null, true, Ct);
        await f.Workflow.TickAsync(Ct);
        Assert.Equal("cancelled", (await f.Runs.GetRunAsync(retry.Id, Ct))!.Status);
        Assert.Single(await f.Runs.GetActiveRunsAsync(Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LaneEditsAffectFutureEntriesAndRetainCurrentReporting(bool clear)
    {
        var f = await NewWorkflow();
        var lane = (await store.GetLaunchLaneAsync(repo, f.Lane, Ct))!;
        await store.SaveLaunchLaneAsync(repo, f.Lane, lane with { Selections = clear ? [] : lane.Selections.Reverse().ToArray() }, Ct);
        await SavePass(f);
        await f.Runs.CompleteAsync(f.Claim, "succeeded", 0, ct: Ct);
        await f.Workflow.TickAsync(Ct);
        Assert.Single(await f.Runs.GetActiveRunsAsync(Ct));
    }

    [Fact]
    public async Task WorkflowRejectsMissingStaleForeignAndOldEntryEvidence()
    {
        var f = await NewWorkflow();
        var author = BoardAuthor.Agent("Codex", "codex", f.Session);
        await Assert.ThrowsAsync<BoardValidationException>(() => f.Workflow.TryReportAsync(repo, card.Id, repo, author, "passed", "Done", f.Entry, null, Ct));
        var report = await reviews.BeginAsync(repo, card.Id, f.Session, repo, "repository", "All", null, null, true, Ct);
        await reviews.SaveAsync(repo, card.Id, f.Session, report.Id, "No findings reported", "None", "Read", "None", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "sample.cs"), "class Changed {}", Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() => f.Workflow.TryReportAsync(repo, card.Id, repo, author, "passed", "Done", f.Entry, report.Id, Ct));
        var other = "other-session";
        await store.LinkSessionAsync(repo, card.Id, other, null, "base:codex", "codex", "Other", "work", Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() => f.Workflow.TryReportAsync(repo, card.Id, repo, BoardAuthor.Agent("Codex", "codex", other), "failed", "Wrong owner", f.Entry, null, Ct));
        await store.MoveCardAsync(repo, card.Id, card.ColumnId, null, Ct);
        Assert.False(await f.Workflow.TryReportAsync(repo, card.Id, repo, author, "failed", "Old entry", f.Entry, null, Ct));
        await f.Workflow.TickAsync(Ct);
        Assert.True((await f.Runs.GetRunAsync(f.Run.Id, Ct))!.CancelRequested);
    }
}
