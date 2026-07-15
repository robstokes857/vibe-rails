using TokenSaver.Pipeline;
using VibeRails.Routes;
using Xunit;

namespace Tests.Routes;

public sealed class CompressionCaptureRoutesTests
{
    [Fact]
    public void RunPipeline_ScopeDisabled_ReturnsRawTextWithoutRunningStages()
    {
        const string raw = "value  \n";

        var (output, trace, scopeAllowed) = CompressionCaptureRoutes.RunPipeline(
            raw,
            command: "echo value",
            provider: "anthropic",
            toolName: "Bash",
            enabledIds: [CompressionCatalog.TrailingWhitespace]);

        Assert.False(scopeAllowed);
        Assert.Equal(raw, output);
        Assert.Empty(trace);
    }

    [Fact]
    public void RunPipeline_ScopeEnabled_RunsTheSelectedStages()
    {
        var (output, trace, scopeAllowed) = CompressionCaptureRoutes.RunPipeline(
            "value  \n",
            command: "echo value",
            provider: "anthropic",
            toolName: "Bash",
            enabledIds:
            [
                CompressionCatalog.TrailingWhitespace,
                CompressionCatalog.ScopeShell,
            ]);

        Assert.True(scopeAllowed);
        Assert.Equal("value\n", output);
        Assert.Contains(trace, stage =>
            stage.StageId == CompressionCatalog.TrailingWhitespace
            && stage.Outcome == StageOutcome.Applied);
    }
}
