using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using Xunit;

namespace Tests.Services.Terminal;

public sealed class BaseLlmOptionsTests
{
    [Theory]
    [InlineData("gpt-6-astra", "fast")]
    [InlineData("gpt-6-astra", "ultrafast")]
    [InlineData("gpt-6.1-sol", "fast")]
    [InlineData("gpt-6-sol", "fast")]
    [InlineData("gpt-6-luna", "fast")]
    [InlineData("gpt-5.6-sol", "fast")]
    [InlineData("gpt-5.6-terra", "fast")]
    [InlineData("gpt-5.6-luna", "fast")]
    [InlineData("gpt-5.5", "fast")]
    public void CodexSpeedBecomesSessionArguments(string model, string speed) =>
        Assert.Equal(["--model", model, "-c", "model_reasoning_effort=high", "-c", $"service_tier={speed}", "--enable", "fast_mode"],
            BaseLlmOptionsBuilder.BuildArguments(LLM.Codex, new(model, "high", Speed: speed)));

    [Theory]
    [InlineData(LLM.Codex, "", "fast")]
    [InlineData(LLM.Codex, "custom-model", "fast")]
    [InlineData(LLM.Codex, "gpt-6.1-sol", "ultrafast")]
    [InlineData(LLM.Codex, "gpt-5.6-sol", "ultrafast")]
    [InlineData(LLM.Codex, "gpt-6-astra", "turbo")]
    [InlineData(LLM.Codex, "gpt-6-astra", "fast --yolo")]
    [InlineData(LLM.Claude, "gpt-6-astra", "fast")]
    public void UnsupportedSpeedsAreRejected(LLM cli, string model, string speed) =>
        Assert.Throws<ArgumentException>(() => BaseLlmOptionsBuilder.BuildArguments(cli, new(model, Speed: speed)));

    [Fact]
    public void DefaultSpeedKeepsExistingConfigAndLegacyOptions()
    {
        Assert.Null(BaseLlmOptionsBuilder.Normalize(LLM.Codex, new(Speed: "default")));
        Assert.Equal(["--model", "custom-model"], BaseLlmOptionsBuilder.BuildArguments(LLM.Codex, new("custom-model")));
        Assert.Equal("fast", BaseLlmOptionsBuilder.Normalize(LLM.Codex, new(" GPT-6-ASTRA ", Speed: " FAST "))!.Speed);
        var legacy = System.Text.Json.JsonSerializer.Deserialize("{\"model\":\"gpt-6-astra\",\"effort\":\"high\",\"yolo\":true}", AppJsonSerializerContext.Default.BaseLlmOptions)!;
        Assert.Null(legacy.Speed);
        Assert.DoesNotContain("fast_mode", BaseLlmOptionsBuilder.BuildArguments(LLM.Codex, legacy));
    }

    [Theory]
    [InlineData(LLM.Claude, "--permission-mode")]
    [InlineData(LLM.Grok46, "--permission-mode")]
    [InlineData(LLM.Antigravity, "--mode")]
    [InlineData(LLM.Copilot, "--mode")]
    [InlineData(LLM.OpenCode, "--agent")]
    [InlineData(LLM.Glm52, "--agent")]
    [InlineData(LLM.Glm53, "--agent")]
    [InlineData(LLM.DeepSeekV4Pro, "--agent")]
    [InlineData(LLM.KimiK3, "--agent")]
    public void PlanModeUsesProviderFlag(LLM cli, string flag) =>
        Assert.Equal([flag, "plan"], BaseLlmOptionsBuilder.BuildArguments(cli, new(Mode: "plan")));

    [Fact]
    public void CodexHasNoStartupMode_AndAStoredOneIsDroppedRatherThanRejected()
    {
        // Codex's /plan is a TUI command with no launch flag. The handshake that used to type it
        // into the running TUI was removed 2026-09-15, so there is no mode to honour — and a card
        // saved before then must still launch, which is why this drops the mode instead of throwing.
        var options = new BaseLlmOptions("gpt-5.5", "max", "plan");
        Assert.Equal(["--model", "gpt-5.5", "-c", "model_reasoning_effort=xhigh"], BaseLlmOptionsBuilder.BuildArguments(LLM.Codex, options));
        Assert.Equal("", BaseLlmOptionsBuilder.Normalize(LLM.Codex, options)!.Mode);
        Assert.Null(BaseLlmOptionsBuilder.Normalize(LLM.Codex, new(Mode: "plan")));
    }

    [Theory]
    [InlineData(LLM.Codex, "--dangerously-bypass-approvals-and-sandbox")]
    [InlineData(LLM.Claude, "--dangerously-skip-permissions")]
    [InlineData(LLM.Antigravity, "--dangerously-skip-permissions")]
    [InlineData(LLM.Copilot, "--yolo")]
    [InlineData(LLM.Grok46, "--yolo")]
    [InlineData(LLM.OpenCode, "--auto")]
    [InlineData(LLM.Glm52, "--auto")]
    [InlineData(LLM.Glm53, "--auto")]
    [InlineData(LLM.DeepSeekV4Pro, "--auto")]
    [InlineData(LLM.KimiK3, "--auto")]
    public void YoloUsesTheProviderLaunchFlag(LLM cli, string flag)
    {
        var normalized = BaseLlmOptionsBuilder.Normalize(cli, new(Yolo: true));
        Assert.NotNull(normalized);
        Assert.True(normalized.Yolo);
        Assert.Equal([flag], BaseLlmOptionsBuilder.BuildArguments(cli, normalized));
    }

    [Fact]
    public void AntigravityModelRemainsOneArgument() =>
        Assert.Equal(["--model", "Gemini 3.5 Flash (Low)", "--effort", "low"],
            BaseLlmOptionsBuilder.BuildArguments(LLM.Antigravity, new("Gemini 3.5 Flash (Low)", "low")));

    [Fact]
    public void FixedModelIsNotDuplicated() =>
        Assert.Equal(["--agent", "build"], BaseLlmOptionsBuilder.BuildArguments(LLM.Glm53, new("zai-coding-plan/glm-5.3", Mode: "build")));

    [Fact]
    public void ClaudeOneMillionContextSuffixIsAccepted()
    {
        // Claude Code reads `claude-fable-5-1[1m]` as the 1M-context form of the full model ID.
        // Behind the VibeRails proxy it budgets 200K for the bare ID, so the catalog pins this form.
        Assert.Equal(["--model", "claude-fable-5-1[1m]"],
            BaseLlmOptionsBuilder.BuildArguments(LLM.Claude, new(Model: "claude-fable-5-1[1m]")));
        Assert.Equal(["--model", "claude-opus-5-5[1m]", "--effort", "max"],
            BaseLlmOptionsBuilder.BuildArguments(LLM.Claude, new("claude-opus-5-5[1m]", "max")));
    }

    [Theory]
    [InlineData(LLM.Codex, "--help", "", "")]
    [InlineData(LLM.Codex, "model; calc", "", "")]
    [InlineData(LLM.Codex, "model\u001b[201~", "", "")]
    [InlineData(LLM.Claude, "[1m]", "", "")]
    [InlineData(LLM.Claude, "[1m]claude-fable-5-1", "", "")]
    [InlineData(LLM.Claude, "claude-fable-5-1[1M]", "", "")]
    [InlineData(LLM.Claude, "claude-fable-5-1[2m]", "", "")]
    [InlineData(LLM.Claude, "claude-fable-5-1[1m]x", "", "")]
    [InlineData(LLM.Claude, "claude[1m]-fable", "", "")]
    // The "[1m]" tail is Claude Code's alone: every other CLI rejects it at validation, not at launch.
    [InlineData(LLM.Codex, "gpt-5.5[1m]", "", "")]
    [InlineData(LLM.Grok46, "grok-4.7[1m]", "", "")]
    [InlineData(LLM.Copilot, "claude-fable-5-1[1m]", "", "")]
    [InlineData(LLM.OpenCode, "anthropic/claude-fable-5-1[1m]", "", "")]
    [InlineData(LLM.Glm53, "zai/glm-5.3", "", "")]
    [InlineData(LLM.Claude, "", "ultra", "")]
    [InlineData(LLM.Grok46, "", "none", "")]
    [InlineData(LLM.Grok46, "", "minimal", "")]
    [InlineData(LLM.Grok46, "", "max", "")]
    [InlineData(LLM.OpenCode, "", "high", "")]
    [InlineData(LLM.Shell, "", "", "plan")]
    public void InvalidOptionsAreRejected(LLM cli, string model, string effort, string mode) =>
        Assert.Throws<ArgumentException>(() => BaseLlmOptionsBuilder.Normalize(cli, new(model, effort, mode)));

    [Fact]
    public void Grok46EffortIsLowMediumHighXhigh()
    {
        Assert.Equal(["--effort", "low"], BaseLlmOptionsBuilder.BuildArguments(LLM.Grok46, new(Effort: "low")));
        Assert.Equal(["--effort", "xhigh"], BaseLlmOptionsBuilder.BuildArguments(LLM.Grok46, new(Effort: "xhigh")));
    }

    [Fact]
    public void GrokModelIsSelectable()
    {
        Assert.Equal(["--model", "grok-4.7"], BaseLlmOptionsBuilder.BuildArguments(LLM.Grok46, new(Model: "grok-4.7")));
        Assert.Equal(["--model", "grok-4.6"], BaseLlmOptionsBuilder.BuildArguments(LLM.Grok46, new(Model: "grok-4.6")));
        Assert.Empty(BaseLlmOptionsBuilder.BuildArguments(LLM.Grok46, new(Model: "")));
    }
}
