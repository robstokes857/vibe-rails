using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.LlmClis;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Mcp;

public sealed partial class BoardToolTests
{
    [Fact]
    public async Task McpAssignment_PersistsAstraXhighUltrafast_AndShowsSavedSettings()
    {
        await _tool.CreateBoardCard("Astra work", cancellationToken: Ct);
        var options = new BaseLlmOptions(Model: "gpt-6-astra", Effort: "xhigh", Speed: "ultrafast");
        var result = await _tool.UpdateBoardCard("PROJ-1", assignee: "base:codex", baseLlmOptions: options, cancellationToken: Ct);

        Assert.DoesNotContain("FAIL:", result);
        Assert.Contains("model=gpt-6-astra; effort=xhigh; speed=ultrafast", result);
        var saved = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!;
        Assert.Equal("base:codex", saved.Assignee);
        Assert.Equal(options with { Mode = "" }, saved.BaseLlmOptions);
        Assert.Equal(["--model", "gpt-6-astra", "-c", "model_reasoning_effort=xhigh", "-c", "service_tier=ultrafast", "-c", "features.fast_mode=true"],
            BaseLlmOptionsBuilder.BuildArguments(LLM.Codex, saved.BaseLlmOptions));
        Assert.Contains("model=gpt-6-astra; effort=xhigh; speed=ultrafast", await _tool.GetBoardCard(saved.Key, cancellationToken: Ct));

        var tabs = new Mock<ITerminalTabHostService>(MockBehavior.Strict);
        var sessionId = Guid.NewGuid().ToString();
        tabs.SetupGet(t => t.MaxTabs).Returns(8);
        tabs.Setup(t => t.ListTabsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([]);
        tabs.Setup(t => t.CreateTabAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TerminalTabStatusResponse("tab-test", DateTime.UtcNow, false));
        StartTerminalRequest? captured = null;
        tabs.Setup(t => t.StartSessionAsync("tab-test", It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Callback<string, StartTerminalRequest, CancellationToken>((_, request, _) => captured = request)
            .ReturnsAsync(new TerminalStatusResponse(true, sessionId, "codex", _project));
        var repository = Mock.Of<IRepository>();
        var launcher = new BoardLaunchService(_store, repository, tabs.Object, new BoardContextEstimator(_service, _store, repository));
        await launcher.LaunchAsync(_project, saved.Id, null, Ct);
        Assert.Equal(saved.BaseLlmOptions, captured!.BaseLlmOptions);
        Assert.Equal("codex", captured.Cli);

        // Ordinary card edits must not reset the launch settings.
        await _tool.UpdateBoardCard(saved.Key, title: "Renamed", cancellationToken: Ct);
        Assert.Equal(saved.BaseLlmOptions, (await _store.FindCardAsync(_project, saved.Key, Ct))!.BaseLlmOptions);
    }

    [Fact]
    public async Task McpCreate_CanAssignOptions_AndUpdateCanReplaceOrClearThem()
    {
        var options = new BaseLlmOptions(Model: "gpt-6-astra", Effort: "xhigh", Speed: "ultrafast");
        var result = await _tool.CreateBoardCard("Astra work", assignee: "base:codex", baseLlmOptions: options, cancellationToken: Ct);
        Assert.Contains("speed=ultrafast", result);
        await _tool.UpdateBoardCard("PROJ-1", baseLlmOptions: new BaseLlmOptions(Model: "gpt-6-astra"), cancellationToken: Ct);
        var card = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!;
        Assert.Equal("", card.BaseLlmOptions!.Speed);
        await _tool.UpdateBoardCard(card.Key, clearBaseLlmOptions: true, cancellationToken: Ct);
        card = (await _store.FindCardAsync(_project, card.Key, Ct))!;
        Assert.Equal("base:codex", card.Assignee);
        Assert.Null(card.BaseLlmOptions);
        await _tool.UpdateBoardCard(card.Key, assignee: "", cancellationToken: Ct);
        Assert.Null((await _store.FindCardAsync(_project, card.Key, Ct))!.Assignee);
    }

    [Theory]
    [InlineData("base:codex", "gpt-6-sol", "ultrafast")]
    [InlineData("base:codex", "gpt-6-astra", "turbo")]
    [InlineData("base:claude", "gpt-6-astra", "ultrafast")]
    [InlineData("env:7:codex", "gpt-6-astra", "ultrafast")]
    [InlineData("", "gpt-6-astra", "ultrafast")]
    public async Task McpAssignment_RejectsUnsupportedOptionsWithoutOtherWrites(string assignee, string model, string speed)
    {
        await _tool.CreateBoardCard("Unchanged", cancellationToken: Ct);
        var result = await _tool.UpdateBoardCard("PROJ-1", title: "Must not save", assignee: assignee,
            baseLlmOptions: new BaseLlmOptions(Model: model, Speed: speed), cancellationToken: Ct);
        Assert.StartsWith("FAIL:", result);
        var card = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!;
        Assert.Equal("Unchanged", card.Title);
        Assert.Null(card.Assignee);
        Assert.Null(card.BaseLlmOptions);
    }

    [Fact]
    public async Task McpAssignment_RejectsConflictingClear_AndClearsOptionsOnProviderChange()
    {
        var options = new BaseLlmOptions(Model: "gpt-6-astra", Speed: "ultrafast");
        await _tool.CreateBoardCard("Astra work", assignee: "base:codex", baseLlmOptions: options, cancellationToken: Ct);
        Assert.StartsWith("FAIL:", await _tool.UpdateBoardCard("PROJ-1", baseLlmOptions: options,
            clearBaseLlmOptions: true, cancellationToken: Ct));
        Assert.Equal(options with { Effort = "", Mode = "" }, (await _store.FindCardAsync(_project, "PROJ-1", Ct))!.BaseLlmOptions);
        await _tool.UpdateBoardCard("PROJ-1", assignee: "base:claude", cancellationToken: Ct);
        var card = (await _store.FindCardAsync(_project, "PROJ-1", Ct))!;
        Assert.Equal("base:claude", card.Assignee);
        Assert.Null(card.BaseLlmOptions);
    }
}
