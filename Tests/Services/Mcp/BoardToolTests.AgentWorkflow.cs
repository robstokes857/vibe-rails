using VibeRails.DTOs;
using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolTests
{
    [Fact]
    public async Task AgentWorkflowToolsSaveAndReadIndependentManagerAndWorkerSettings()
    {
        var options = new BaseLlmOptions(Model: "gpt-6-astra", Speed: "ultrafast");
        var created = await _tool.CreateBoardCard("Managed", assignee: "base:claude", workflowMode: "agent",
            managerSelection: "base:codex", managerBaseLlmOptions: options, cancellationToken: Ct);
        Assert.DoesNotContain("FAIL:", created);
        Assert.Contains("Workflow: agent", created);
        var card = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!;
        Assert.Equal("base:claude", card.Assignee);
        Assert.Equal("base:codex", card.ManagerSelection);
        var read = await _tool.GetBoardCard(card.Key, cancellationToken: Ct);
        Assert.Contains("Workflow: agent", read);
        Assert.Contains("intent=worker", read);
        Assert.Contains("speed=ultrafast", read);
        Assert.StartsWith("FAIL:", await _tool.UpdateBoardCard(card.Key, title: "Must not save",
            managerSelection: "", cancellationToken: Ct));
        Assert.Equal("Managed", (await _store.FindCardAsync(_project, card.Key, Ct))!.Title);
        var updated = await _tool.UpdateBoardCard(card.Key, managerSelection: "base:claude", cancellationToken: Ct);
        Assert.DoesNotContain("FAIL:", updated);
        Assert.Null((await _store.FindCardAsync(_project, card.Key, Ct))!.ManagerBaseLlmOptions);
        Assert.DoesNotContain("FAIL:", await _tool.UpdateBoardCard(card.Key, workflowMode: "manual", managerSelection: "", cancellationToken: Ct));
    }
}
