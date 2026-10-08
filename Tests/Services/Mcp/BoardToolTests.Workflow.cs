using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolTests
{
    [Fact]
    public async Task WorkflowToolRequiresManagedSessionAndNeverAllowsAgentSkip()
    {
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var card = await _store.CreateCardAsync(_project, new(null, "Workflow", "", null, "medium", null, [], false), Ct);
        Assert.Contains("current VibeRails agent session", await _tool.ReportAutomationStep("failed", "Blocked", card: card.Key, cancellationToken: Ct));
        _resolver.CurrentSessionId = Guid.NewGuid().ToString();
        await _store.LinkSessionAsync(_project, card.Id, _resolver.CurrentSessionId, null, "base:codex", "codex", "Worker", "work", Ct);
        Assert.Contains("Only the user can skip", await _tool.ReportAutomationStep("skipped", "Bypass", card: card.Key, cancellationToken: Ct));
        Assert.Empty((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Comments);
    }

    [Fact]
    public async Task WorkflowToolRecordsOnlyTheCurrentRunsDecision()
    {
        var repository = new Repository(_stateConnectionString);
        var env = await repository.SaveEnvironmentAsync(new() { CustomName = "Worker", LLM = LLM.Codex }, Ct);
        var job = await _jobs.CreateJobAsync(new("Work", _project, LLM.Codex, env.Id, "Work", null, true, []), Ct);
        await _store.EnsureDefaultColumnsAsync(_project, Ct);
        var lanes = await _store.GetColumnsAsync(_project, Ct);
        await _store.SaveLaneAutomationAsync(_project, lanes[0].Id, [job.Id], 0, Ct);
        var card = await _store.CreateCardAsync(_project, new(lanes[0].Id, "Workflow", "", null, "medium", null, [], false), Ct);
        var run = Assert.Single(await _jobs.EnqueueDueSchedulesAsync(DateTime.UtcNow.AddMinutes(2), Ct));
        _resolver.CurrentSessionId = Guid.NewGuid().ToString();
        await repository.CreateSessionAsync(_resolver.CurrentSessionId, "codex", "Worker", _project, 1234, run);
        await _store.LinkSessionAsync(_project, card.Id, _resolver.CurrentSessionId, null, "base:codex", "codex", "Worker", "automation", Ct);
        await _jobs.StartRunAsync(run, 1234, Ct);
        Assert.StartsWith("Workflow status recorded", await _tool.ReportAutomationStep("failed", "Needs correction", card: card.Key, cancellationToken: Ct));
        var step = Assert.Single(await _store.GetLaneAutomationStatusesAsync(_project, card.Id, Ct));
        Assert.Equal("Failed", step.StepStatus);
        Assert.Contains("Needs correction", Assert.Single((await _store.GetCardDetailAsync(_project, card.Id, Ct))!.Comments).Body);
        await _store.MoveCardAsync(_project, card.Id, lanes[1].Id, null, Ct);
        await _store.MoveCardAsync(_project, card.Id, lanes[0].Id, null, Ct);
        Assert.StartsWith("FAIL:", await _tool.ReportAutomationStep("passed", "Old result", eventKey: step.EventKey, card: card.Key, cancellationToken: Ct));
    }
}
