using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

public sealed partial class BoardSettingsTests
{
    [Theory]
    [InlineData("Passed", null, "Passed")]
    [InlineData("Completed with limitations", null, "Completed with limitations")]
    [InlineData("Skipped/not applicable", null, "Skipped/not applicable")]
    [InlineData("Findings", null, "Findings")]
    [InlineData("Passed", "Skipped/not applicable", "Completed with limitations")]
    [InlineData("Skipped/not applicable", "Skipped/not applicable", "Skipped/not applicable")]
    [InlineData("Completed with limitations", "Findings", "Findings")]
    [InlineData("Failed to run", null, "Failed to run")]
    [InlineData("Cancelled", null, "Cancelled")]
    public async Task CheckOnlyLaneDisplaysSavedOutcomeAndKeepsExecutionGating(string status, string? second, string expected)
    {
        var (_, lane, _, _) = await Lanes();
        var actions = new List<JobActionRequest> { new(null, JobActionKind.CodeQuality, Arguments: ["working-tree"]) };
        if (second is not null) actions.Add(new(null, JobActionKind.Vca, Arguments: ["working-tree"]));
        var job = await _jobs.CreateJobAsync(new("Checks", _root, LLM.NotSet, null, "", null, true, [], Actions: actions), Ct);
        var next = await Job("Next");
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id, next.Id], 0, Ct);
        var card = await Card(lane);
        var due = await Due(card.Id);
        var runId = Assert.Single(await Tick(due));
        var runActions = await _jobs.GetRunActionsAsync(runId, Ct);
        for (var i = 0; i < runActions.Count; i++)
            Assert.True(await _boards.SaveCheckAsync(_root, Check(card.Id, runId, runActions[i].Id,
                i == 0 ? status : second!), Ct));
        var failed = status is "Failed to run" or "Cancelled";
        await _jobs.CompleteRunAsync(runId, failed ? JobRunStatus.Failed : JobRunStatus.Succeeded, failed ? 1 : 0, null, Ct);

        var entry = (await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct)).Single(e => e.JobId == job.Id);
        Assert.Equal(expected, entry.DisplayStatus);
        Assert.Equal(failed ? "Failed" : "Passed", entry.StepStatus);
        Assert.Contains("5 files analyzed", entry.Reason);
        Assert.DoesNotContain("Automation run succeeded", entry.Reason);
        var batch = Assert.Single(await _boards.GetLaneWorkflowsAsync(_root, lane, Ct));
        Assert.Equal(expected, batch.Steps.Single(e => e.JobId == job.Id).DisplayStatus);
        var nextRuns = await Tick(due + 1);
        Assert.Equal(failed ? 0 : 1, nextRuns.Count);
    }

    [Fact]
    public async Task RetryDoesNotReusePreviousCheckEvidenceOrClaimAMissingResultPassed()
    {
        var (_, lane, _, _) = await Lanes();
        var job = await _jobs.CreateJobAsync(new("Checks", _root, LLM.NotSet, null, "", null, true, [],
            Actions: [new(null, JobActionKind.CodeQuality, Arguments: ["working-tree"])]), Ct);
        await _boards.SaveLaneAutomationAsync(_root, lane, [job.Id], 0, Ct);
        var card = await Card(lane);
        var original = Assert.Single(await Tick(await Due(card.Id)));
        var action = Assert.Single(await _jobs.GetRunActionsAsync(original, Ct));
        Assert.True(await _boards.SaveCheckAsync(_root, Check(card.Id, original, action.Id, "Failed to run"), Ct));
        await _jobs.CompleteRunAsync(original, JobRunStatus.Failed, 1, "failure", Ct);
        var retry = (await _jobs.EnqueueRetryAsync(original, Ct))!;
        Assert.Equal("Queued", Assert.Single(await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct)).DisplayStatus);
        await _jobs.CompleteRunAsync(retry, JobRunStatus.Succeeded, 0, null, Ct);
        var entry = Assert.Single(await _boards.GetLaneAutomationStatusesAsync(_root, card.Id, Ct));
        Assert.Equal(retry, entry.RunId);
        Assert.Equal("Unknown", entry.DisplayStatus);
        Assert.Contains("missing or incomplete", entry.Reason);
    }

    private static BoardCheckRecord Check(string card, string run, string action, string status) =>
        new(Guid.NewGuid().ToString("N"), card, run, action, "Code quality", status, "working-tree",
            null, null, null, null, "1", DateTime.UtcNow, DateTime.UtcNow,
            "5 files analyzed; 0 files with code-quality findings; 7 skipped. Advisory analysis.", 12, 5, 7, 0, []);
}
