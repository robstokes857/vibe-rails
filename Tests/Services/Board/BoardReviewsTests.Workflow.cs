using Moq;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardReviewsTests
{
    private async Task<(long ReviewJob, long NextJob, string RunId, string Session, string EventKey)> Workflow()
    {
        var env = await repository.SaveEnvironmentAsync(new() { CustomName = "Workflow reviewer",
            LLM = LLM.Codex, Purpose = "code_review", AutomationWorker = true }, Ct);
        var first = await jobs.CreateJobAsync(new("Review", repo, LLM.Codex, env.Id, "Review", null, true, []), Ct);
        var next = await jobs.CreateJobAsync(new("VCA", repo, LLM.NotSet, null, "", null, true, [],
            Actions: [new(null, JobActionKind.Vca, Arguments: ["working-tree"])]), Ct);
        var lane = (await store.GetColumnsAsync(repo, Ct)).Single(c => c.Name == "Review");
        await store.SaveLaneAutomationAsync(repo, lane.Id, [first.Id, next.Id], 0, Ct);
        await store.MoveCardAsync(repo, card.Id, lane.Id, null, Ct);
        var runId = Assert.Single(await jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(2), Ct));
        var session = await LinkWorkflowRun(runId);
        var entry = (await store.GetLaneAutomationStatusesAsync(repo, card.Id, Ct)).Single(e => e.JobId == first.Id);
        return (first.Id, next.Id, runId, session, entry.EventKey);
    }

    private async Task<string> LinkWorkflowRun(string runId)
    {
        var run = (await jobs.GetRunAsync(runId, Ct))!;
        var session = Guid.NewGuid().ToString();
        await jobs.StartRunAsync(runId, 1234, Ct);
        await repository.CreateSessionAsync(session, "codex", "Workflow reviewer", repo, 1234, runId);
        await jobs.LinkRunActionSessionAsync(runId, run.Actions!.Single(a => a.Kind == JobActionKind.Worker).Id, session, Ct);
        await service.LinkSessionAsync(repo, card.Id, session, null, "base:codex", "codex", "Reviewer",
            BoardSessionRecord.AutomationOrigin, Ct);
        return session;
    }

    [Theory]
    [InlineData("resave")]
    [InlineData("reorder")]
    [InlineData("clear")]
    public async Task SettingsSaveKeepsRunningAgentReportableAndSkippable(string change)
    {
        var flow = await Workflow();
        var lane = (await store.FindCardAsync(repo, card.Id, Ct))!.ColumnId;
        var before = (await store.GetLaneAutomationStatusesAsync(repo, card.Id, Ct))[0];
        long[] selection = change == "clear" ? [] : change == "reorder"
            ? [flow.NextJob, flow.ReviewJob] : [flow.ReviewJob, flow.NextJob];
        await store.SaveLaneAutomationAsync(repo, lane, selection, 1, Ct);
        var steps = Assert.Single(await store.GetLaneWorkflowsAsync(repo, lane, Ct)).Steps;
        Assert.Equal(before.WorkflowId, steps[0].WorkflowId);
        Assert.True(steps[0].CanSkip);
        Assert.Equal("Reviewing", steps[0].StepStatus);
        Assert.Equal("Cancelled", steps[1].StepStatus);
        var workflow = new BoardWorkflowService(store, reviews);
        await workflow.ReportAsync(repo, card.Id, repo, BoardAuthor.Agent("Codex", "codex", flow.Session),
            "failed", "Findings recorded after settings save", null, null, Ct);
        await new BoardCardAutomationService(store, jobs, Mock.Of<VibeRails.Services.Jobs.IJobService>())
            .SkipAsync(repo, card.Id, flow.ReviewJob, flow.EventKey, Ct);
        Assert.True(await jobs.IsCancelRequestedAsync(flow.RunId, Ct));
        await jobs.CompleteRunAsync(flow.RunId, JobRunStatus.Cancelled, 1, "Stopped", Ct);
        Assert.Empty(await jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(3), Ct));
        Assert.Empty(await jobs.GetRunsAsync(flow.NextJob, cancellationToken: Ct));
    }

    [Fact]
    public async Task WorkflowReviewRequiresExplicitPassAndRunCompletion()
    {
        var flow = await Workflow();
        var workflow = new BoardWorkflowService(store, reviews);
        var author = BoardAuthor.Agent("Codex", "codex", flow.Session);
        var report = await reviews.BeginAsync(repo, card.Id, flow.Session, repo, "repository", "All code", null, null, true, Ct);
        await reviews.SaveAsync(repo, card.Id, flow.Session, report.Id, "Findings", "sample.cs:1 Low: optional naming improvement.", "Inspected", "None", Ct);
        Assert.Empty(await jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(3), Ct));
        await workflow.ReportAsync(repo, card.Id, repo, author, "passed", "No blocking findings.", null, report.Id, Ct);
        Assert.Empty(await jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(3), Ct));
        await jobs.CompleteRunAsync(flow.RunId, JobRunStatus.Succeeded, 0, null, Ct);
        var next = Assert.Single(await jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(3), Ct));
        Assert.Equal(flow.NextJob, (await jobs.GetRunAsync(next, Ct))!.JobId);
        var steps = await store.GetLaneAutomationStatusesAsync(repo, card.Id, Ct);
        Assert.Equal("Passed", steps[0].StepStatus);
        Assert.Equal("Queued", steps[1].StepStatus);
        Assert.Equal(new[] { flow.ReviewJob, flow.NextJob }, steps.Select(s => s.JobId));
    }

    [Fact]
    public async Task SuccessfulReviewExitWithoutVerdictDoesNotReleaseNextStep()
    {
        var flow = await Workflow();
        await jobs.CompleteRunAsync(flow.RunId, JobRunStatus.Succeeded, 0, null, Ct);
        Assert.Empty(await jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(3), Ct));
        Assert.Equal("Awaiting result", (await store.GetLaneAutomationStatusesAsync(repo, card.Id, Ct))[0].StepStatus);
    }

    [Fact]
    public async Task ReviewingWithoutVerdictDoesNotRemainActiveAfterExit()
    {
        var flow = await Workflow();
        var workflow = new BoardWorkflowService(store, reviews);
        await workflow.ReportAsync(repo, card.Id, repo, BoardAuthor.Agent("Codex", "codex", flow.Session),
            "reviewing", "Checking the changes", null, null, Ct);
        await jobs.CompleteRunAsync(flow.RunId, JobRunStatus.Succeeded, 0, null, Ct);
        Assert.Equal("Awaiting result", (await store.GetLaneAutomationStatusesAsync(repo, card.Id, Ct))[0].StepStatus);
        Assert.Empty(await jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(3), Ct));
    }

    [Fact]
    public async Task PassFollowedByProcessFailureCanBeRetriedAndSkippedWhileRetryRuns()
    {
        var flow = await Workflow();
        var workflow = new BoardWorkflowService(store, reviews);
        var report = await reviews.BeginAsync(repo, card.Id, flow.Session, repo, "repository", "All code", null, null, true, Ct);
        await reviews.SaveAsync(repo, card.Id, flow.Session, report.Id, "No findings reported", "None", "Inspected", "None", Ct);
        await workflow.ReportAsync(repo, card.Id, repo, BoardAuthor.Agent("Codex", "codex", flow.Session),
            "passed", "No blockers", null, report.Id, Ct);
        await jobs.CompleteRunAsync(flow.RunId, JobRunStatus.Failed, 1, "Process failed", Ct);
        Assert.Equal("Failed", (await store.GetLaneAutomationStatusesAsync(repo, card.Id, Ct))[0].StepStatus);
        Assert.Empty(await jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(3), Ct));
        var retry = (await jobs.EnqueueBoardCardRunAsync(repo, flow.ReviewJob, card.Key, Ct))!;
        var reviewer = await LinkWorkflowRun(retry);
        await workflow.ReportAsync(repo, card.Id, repo, BoardAuthor.Agent("Codex", "codex", reviewer),
            "reviewing", "Retrying review", flow.EventKey, null, Ct);
        var step = (await store.GetLaneAutomationStatusesAsync(repo, card.Id, Ct))[0];
        Assert.Equal("Reviewing", step.StepStatus);
        Assert.Equal(retry, step.RunId);
        var automations = new BoardCardAutomationService(store, jobs, Mock.Of<VibeRails.Services.Jobs.IJobService>());
        await automations.SkipAsync(repo, card.Id, flow.ReviewJob, flow.EventKey, Ct);
        Assert.True(await jobs.IsCancelRequestedAsync(retry, Ct));
        Assert.Equal("Stopping", (await store.GetLaneAutomationStatusesAsync(repo, card.Id, Ct))[0].StepStatus);
        Assert.Empty(await jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(3), Ct));
        await jobs.CompleteRunAsync(retry, JobRunStatus.Cancelled, 1, "Stopped", Ct);
        Assert.Single(await jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(3), Ct));
    }

    [Fact]
    public async Task WorkflowRejectsStaleEvidenceAndUnrelatedOrOldSessions()
    {
        var flow = await Workflow();
        var workflow = new BoardWorkflowService(store, reviews);
        var author = BoardAuthor.Agent("Codex", "codex", flow.Session);
        await Assert.ThrowsAsync<BoardValidationException>(() =>
            workflow.ReportAsync(repo, card.Id, repo, author, "passed", "Done", null, null, Ct));
        var report = await reviews.BeginAsync(repo, card.Id, flow.Session, repo, "repository", "All code", null, null, true, Ct);
        await reviews.SaveAsync(repo, card.Id, flow.Session, report.Id, "No findings reported", "None", "Inspected", "None", Ct);
        await File.WriteAllTextAsync(Path.Combine(repo, "sample.cs"), "class Changed {}\n", Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() =>
            workflow.ReportAsync(repo, card.Id, repo, author, "passed", "Done", null, report.Id, Ct));
        var other = Guid.NewGuid().ToString();
        await service.LinkSessionAsync(repo, card.Id, other, null, "base:codex", "codex", "Worker", "work", Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() =>
            workflow.ReportAsync(repo, card.Id, repo, BoardAuthor.Agent("Codex", "codex", other), "passed", "Done", flow.EventKey, report.Id, Ct));
        var lane = (await store.FindCardAsync(repo, card.Id, Ct))!.ColumnId;
        await store.MoveCardAsync(repo, card.Id, card.ColumnId, null, Ct);
        await store.MoveCardAsync(repo, card.Id, lane, null, Ct);
        await Assert.ThrowsAsync<BoardValidationException>(() =>
            workflow.ReportAsync(repo, card.Id, repo, author, "failed", "Old run", flow.EventKey, null, Ct));
        Assert.DoesNotContain(await store.GetLaneWorkflowsAsync(repo + "-foreign", lane, Ct), _ => true);
    }

    [Fact]
    public async Task FailedStepCanShowFixingAndPassThroughAFreshRun()
    {
        var flow = await Workflow();
        var workflow = new BoardWorkflowService(store, reviews);
        var author = BoardAuthor.Agent("Codex", "codex", flow.Session);
        await workflow.ReportAsync(repo, card.Id, repo, author, "failed", "Blocking bug", null, null, Ct);
        await jobs.CompleteRunAsync(flow.RunId, JobRunStatus.Succeeded, 0, null, Ct);
        var worker = Guid.NewGuid().ToString();
        await service.LinkSessionAsync(repo, card.Id, worker, null, "base:codex", "codex", "Worker", "work", Ct);
        await workflow.ReportAsync(repo, card.Id, repo, BoardAuthor.Agent("Codex", "codex", worker), "fixing", "Fixing the blocker", flow.EventKey, null, Ct);
        Assert.Equal("Fixing", (await store.GetLaneAutomationStatusesAsync(repo, card.Id, Ct))[0].StepStatus);
        Assert.Empty(await jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(3), Ct));
        var retry = (await jobs.EnqueueBoardCardRunAsync(repo, flow.ReviewJob, card.Key, Ct))!;
        var reviewer = await LinkWorkflowRun(retry);
        var report = await reviews.BeginAsync(repo, card.Id, reviewer, repo, "repository", "Fixed code", null, null, true, Ct);
        await reviews.SaveAsync(repo, card.Id, reviewer, report.Id, "No findings reported", "None", "Checked fix", "None", Ct);
        await workflow.ReportAsync(repo, card.Id, repo, BoardAuthor.Agent("Codex", "codex", reviewer), "passed", "Fix verified", flow.EventKey, report.Id, Ct);
        Assert.Empty(await jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(3), Ct));
        await jobs.CompleteRunAsync(retry, JobRunStatus.Succeeded, 0, null, Ct);
        Assert.Single(await jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(3), Ct));
    }

    [Fact]
    public async Task WorkflowMcpRejectsSkipAndReportsTheOwningAgentsFailure()
    {
        var flow = await Workflow();
        var projects = new Mock<IBoardProjectResolver>();
        projects.Setup(p => p.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(repo);
        projects.SetupGet(p => p.CurrentSessionId).Returns(flow.Session);
        projects.SetupGet(p => p.GitWorkingDirectory).Returns(repo);
        var tool = new BoardTool(service, projects.Object, store, new BoardWorkflowService(store, new BoardReviewService(store, service)), reviews);
        Assert.StartsWith("FAIL:", await tool.ReportAutomationStep("skipped", "Bypass", card: card.Key, cancellationToken: Ct));
        Assert.StartsWith("Workflow status recorded", await tool.ReportAutomationStep("failed", "Blocking bug", card: card.Key, cancellationToken: Ct));
        Assert.Equal("Failed", (await store.GetLaneAutomationStatusesAsync(repo, card.Id, Ct))[0].StepStatus);
    }
}
