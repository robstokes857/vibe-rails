using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using Xunit;

namespace Tests.Services.Terminal;

public sealed class BaseLlmOptionsTests
{
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

    [Fact]
    public void AntigravityModelRemainsOneArgument() =>
        Assert.Equal(["--model", "Gemini 3.5 Flash (Low)", "--effort", "low"],
            BaseLlmOptionsBuilder.BuildArguments(LLM.Antigravity, new("Gemini 3.5 Flash (Low)", "low")));

    [Fact]
    public void FixedModelIsNotDuplicated() =>
        Assert.Equal(["--agent", "build"], BaseLlmOptionsBuilder.BuildArguments(LLM.Glm53, new("zai-coding-plan/glm-5.3", Mode: "build")));

    [Theory]
    [InlineData(LLM.Codex, "--help", "", "")]
    [InlineData(LLM.Codex, "model; calc", "", "")]
    [InlineData(LLM.Codex, "model\u001b[201~", "", "")]
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
