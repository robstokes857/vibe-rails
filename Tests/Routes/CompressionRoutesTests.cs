using Microsoft.AspNetCore.Http.HttpResults;
using TokenSaver.Pipeline;
using VibeRails.DTOs;
using VibeRails.Routes;
using Xunit;

namespace Tests.Routes;

public sealed class CompressionRoutesTests
{
    [Fact]
    public void RunPipeline_ScopeDisabled_ReturnsRawTextWithoutRunningStages()
    {
        const string raw = "value  \n";

        var (output, trace, scopeAllowed) = CompressionRoutes.RunPipeline(
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
        var (output, trace, scopeAllowed) = CompressionRoutes.RunPipeline(
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

    [Fact]
    public void Preview_RunsThePipelineOverSuppliedText()
    {
        // plan_1A A3: exchange-mined candidate text hits the real pipeline via one request
        // instead of a traffic-reproduction session.
        var result = CompressionRoutes.Preview(
            new CompressionPreviewRequest(
                Text: "value  \n",
                ToolName: "Bash",
                Command: "echo value",
                Provider: "anthropic",
                EnabledIds: [CompressionCatalog.TrailingWhitespace, CompressionCatalog.ScopeShell]));

        var ok = Assert.IsType<Ok<CompressionPreviewResponse>>(result);
        var response = ok.Value!;
        Assert.Equal("value\n", response.Output);
        Assert.Equal(8, response.CharsBefore);
        Assert.Equal(6, response.CharsAfter);
        Assert.True(response.ScopeAllowed);
        Assert.Contains(response.Trace, stage =>
            stage.StageId == CompressionCatalog.TrailingWhitespace && stage.Outcome == "Applied");
    }

    [Fact]
    public void Preview_NonAllowlistedTool_ReturnsRawWithScopeDisallowed()
    {
        var result = CompressionRoutes.Preview(
            new CompressionPreviewRequest(
                Text: "value  \n",
                ToolName: "Read",
                Command: null,
                Provider: "anthropic",
                EnabledIds: null));

        var ok = Assert.IsType<Ok<CompressionPreviewResponse>>(result);
        var response = ok.Value!;
        Assert.False(response.ScopeAllowed);
        Assert.Equal("value  \n", response.Output);
        Assert.Empty(response.Trace);
    }

    [Fact]
    public void Preview_MissingOrBlankText_IsRejected()
    {
        var missing = CompressionRoutes.Preview(
            new CompressionPreviewRequest(null, "Bash", null, "anthropic", null));
        var blank = CompressionRoutes.Preview(
            new CompressionPreviewRequest("   ", "Bash", null, "anthropic", null));

        Assert.IsType<BadRequest<ErrorResponse>>(missing);
        Assert.IsType<BadRequest<ErrorResponse>>(blank);
    }

    /// <summary>
    /// The text is bounded only by the request body limit, and the pipeline allocates scratch
    /// proportional to its input.
    /// </summary>
    [Fact]
    public void Preview_RejectsOversizedText()
    {
        var result = CompressionRoutes.Preview(
            new CompressionPreviewRequest(new string('x', 2_000_001), "Bash", null, "anthropic", null));

        Assert.IsType<BadRequest<ErrorResponse>>(result);
    }

    [Fact]
    public void Preview_RequiresToolNameAndProvider()
    {
        var noTool = CompressionRoutes.Preview(
            new CompressionPreviewRequest("text", null, null, "anthropic", null));
        var noProvider = CompressionRoutes.Preview(
            new CompressionPreviewRequest("text", "Bash", null, null, null));

        Assert.IsType<BadRequest<ErrorResponse>>(noTool);
        Assert.IsType<BadRequest<ErrorResponse>>(noProvider);
    }
}
