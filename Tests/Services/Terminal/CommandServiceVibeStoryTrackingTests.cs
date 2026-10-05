using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Terminal;

public partial class CommandServiceTests
{
    public static TheoryData<LLM, string?> StoryTrackingLaunches
    {
        get
        {
            var data = new TheoryData<LLM, string?>();
            foreach (var llm in CommandService.McpClis)
            {
                data.Add(llm, null);
                data.Add(llm, "saved-tracking-env");
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(StoryTrackingLaunches))]
    public async Task StoryTracking_LaunchesWithoutAnInitialMessageReceiveOptionalGuidance(LLM llm, string? envName)
    {
        var service = CreateService(createVibeStoryTrackingEnabled: () => new Settings().CreateVibeStoryTracking);
        var prepared = await service.PrepareSessionAsync(llm, envName, null);

        var prompt = Assert.Single(prepared.Argv!, arg => arg.Contains("Creating a story is optional and at your discretion."));
        Assert.Contains("create_board_card", prompt);
        Assert.Contains("Reuse a story already tracking this work", prompt);
        Assert.Contains("add_board_comment", prompt);
        Assert.Contains("attach_board_session", prompt);
        Assert.Contains("wait for the user's request", prompt);
        Assert.Contains("Creating a story is optional and at your discretion.", prepared.LaunchCommand);
        Assert.DoesNotContain(prepared.Argv!, arg => arg.Contains(".approval_mode=") || arg.StartsWith("--allowedTools")
            || arg.StartsWith("--allow-tool") || arg.StartsWith("--allow="));
        Assert.False(prepared.Environment.ContainsKey("OPENCODE_PERMISSION"));
    }

    [Theory]
    [MemberData(nameof(StoryTrackingLaunches))]
    public async Task StoryTracking_PreservesTheInitialTaskAndUsesTheProviderPromptArgument(LLM llm, string? envName)
    {
        const string task = "Fix the user's \"quoted\" path; keep $value and `literal` intact.";
        var prepared = await CreateService(createVibeStoryTrackingEnabled: () => true)
            .PrepareSessionAsync(llm, envName, ["--model", "chosen"], initialPrompt: task);

        var expectedPrompt = task;
        var expectedArgument = llm switch
        {
            LLM.Copilot => "--interactive=" + expectedPrompt,
            LLM.Antigravity => "--prompt-interactive=" + expectedPrompt,
            LLM.OpenCode or LLM.Glm52 or LLM.Glm53 or LLM.DeepSeekV4Pro or LLM.KimiK3 => "--prompt=" + expectedPrompt,
            _ => expectedPrompt
        };
        Assert.Equal(expectedArgument, prepared.Argv![^1]);
        Assert.DoesNotContain("Creating a story is optional", prepared.LaunchCommand);
        Assert.Contains("--model", prepared.Argv);
        Assert.Contains("chosen", prepared.Argv);
    }

    [Theory]
    [MemberData(nameof(StoryTrackingLaunches))]
    public async Task StoryTracking_DisabledDoesNotSupplyAnInitialPrompt(LLM llm, string? envName)
    {
        var prepared = await CreateService(createVibeStoryTrackingEnabled: () => false)
            .PrepareSessionAsync(llm, envName, null);

        Assert.DoesNotContain("create_board_card", prepared.LaunchCommand);
        Assert.DoesNotContain(prepared.Argv!, arg => arg.Contains("VibeRails:"));
    }

    [Fact]
    public async Task StoryTracking_PreservesSummaryPrecedenceAndExistingBoardAuthorization()
    {
        var prepared = await CreateService(createVibeStoryTrackingEnabled: () => true)
            .PrepareSessionAsync(LLM.Claude, null, null, initialPrompt: "Old task", summary: "Continue the existing story",
                authorizeBoardTools: true);

        Assert.Equal("--", prepared.Argv![^2]);
        Assert.Equal("Continue the existing story", prepared.Argv[^1]);
        Assert.DoesNotContain("Old task", prepared.Argv[^1]);
        Assert.DoesNotContain("Creating a story is optional", prepared.LaunchCommand);
        Assert.Contains(prepared.Argv, arg => arg.StartsWith("--allowedTools="));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n\t")]
    public async Task StoryTracking_BlankInitialMessageAndSummaryUseTheDefaultPrompt(string? initialPrompt)
    {
        var prepared = await CreateService(createVibeStoryTrackingEnabled: () => true)
            .PrepareSessionAsync(LLM.Codex, null, null, initialPrompt: initialPrompt, summary: " \r\n\t");

        Assert.Equal(VibeStoryTrackingPrompt.Guidance, prepared.Argv![^1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoryTracking_BoardAndAutomationPromptsRemainUnchanged(bool trackingEnabled)
    {
        var card = new BoardCardRecord("card_1", "/p", 12, "col_build", 0, "Fix prompts", "Keep custom instructions",
            "base:codex", "medium", null, [], false, 0, DateTime.UtcNow, DateTime.UtcNow);
        var prompts = new[]
        {
            BoardPromptComposer.Compose(card, "Build", "codex", null),
            BoardPromptComposer.ComposeDiscussion(card, null, "Explain this task"),
            BoardPromptComposer.Compose(card, "Review", "codex", null, intent: "code_review"),
            BoardPromptComposer.ComposeAutomationPrompt("VB-12", "Implement this task"),
            BoardPromptComposer.ComposeAutomationPrompt("VB-12", "Review this task", "code_review"),
            BoardPromptComposer.ComposeStandaloneAutomationPrompt(null)
        };
        var service = CreateService(createVibeStoryTrackingEnabled: () => trackingEnabled);
        foreach (var prompt in prompts)
        {
            var prepared = await service.PrepareSessionAsync(LLM.Codex, null, null, initialPrompt: prompt);

            Assert.Equal(prompt, prepared.Argv![^1]);
            Assert.DoesNotContain("Creating a story is optional", prepared.LaunchCommand);
        }
    }

    [Fact]
    public async Task StoryTracking_RechecksSavedSettingsForEachNewLaunch()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"viberails-story-launch-{Guid.NewGuid():N}");
        try
        {
            using var store = new SettingsFile(Path.Combine(directory, "settings.json"));
            store.Save(new Settings());
            var service = CreateService(createVibeStoryTrackingEnabled: () => store.LoadFresh().CreateVibeStoryTracking);
            var enabled = await service.PrepareSessionAsync(LLM.Codex, null, null);
            Assert.Contains("create_board_card", enabled.Argv![^1]);

            // A separate root saves the setting after this service has already launched a CLI.
            using var otherRoot = new SettingsFile(Path.Combine(directory, "settings.json"));
            otherRoot.Save(new Settings { CreateVibeStoryTracking = false });
            var disabled = await service.PrepareSessionAsync(LLM.Codex, null, null);
            Assert.Empty(disabled.Argv!);
            Assert.DoesNotContain("create_board_card", disabled.LaunchCommand);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task StoryTracking_DoesNotApplyToPlainShells()
    {
        var prepared = await CreateService(createVibeStoryTrackingEnabled: () => throw new InvalidOperationException())
            .PrepareSessionAsync(LLM.Shell, null, null);
        Assert.Empty(prepared.LaunchCommand);
        Assert.Null(prepared.Argv);
    }
}
