using global::TokenSaver;
using Microsoft.AspNetCore.Http.HttpResults;
using TokenSaver.Pipeline;
using VibeRails.DB;
using VibeRails.DTOs;
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

    [Fact]
    public async Task Preview_TextForm_RunsThePipelineWithoutTouchingTheStore()
    {
        // plan_1A A3: exchange-mined candidate text hits the real pipeline via one request
        // instead of a traffic-reproduction session.
        var store = new StubCaptureStore();

        var result = await CompressionCaptureRoutes.PreviewAsync(
            new CompressionPreviewRequest(
                CaptureId: null,
                Text: "value  \n",
                ToolName: "Bash",
                Command: "echo value",
                Provider: "anthropic",
                EnabledIds: [CompressionCatalog.TrailingWhitespace, CompressionCatalog.ScopeShell]),
            store,
            TestContext.Current.CancellationToken);

        var ok = Assert.IsType<Ok<CompressionPreviewResponse>>(result);
        var response = ok.Value!;
        Assert.Equal("value\n", response.Output);
        Assert.Equal(8, response.CharsBefore);
        Assert.Equal(6, response.CharsAfter);
        Assert.True(response.ScopeAllowed);
        Assert.Contains(response.Trace, stage =>
            stage.StageId == CompressionCatalog.TrailingWhitespace && stage.Outcome == "Applied");
        Assert.Equal(0, store.GetCalls);
    }

    [Fact]
    public async Task Preview_TextForm_NonAllowlistedTool_ReturnsRawWithScopeDisallowed()
    {
        var result = await CompressionCaptureRoutes.PreviewAsync(
            new CompressionPreviewRequest(
                CaptureId: null,
                Text: "value  \n",
                ToolName: "Read",
                Command: null,
                Provider: "anthropic",
                EnabledIds: null),
            new StubCaptureStore(),
            TestContext.Current.CancellationToken);

        var ok = Assert.IsType<Ok<CompressionPreviewResponse>>(result);
        var response = ok.Value!;
        Assert.False(response.ScopeAllowed);
        Assert.Equal("value  \n", response.Output);
        Assert.Empty(response.Trace);
    }

    [Fact]
    public async Task Preview_BothOrNeitherForm_IsRejected()
    {
        var both = await CompressionCaptureRoutes.PreviewAsync(
            new CompressionPreviewRequest(Guid.NewGuid(), "text", "Bash", null, "anthropic", null),
            new StubCaptureStore(),
            TestContext.Current.CancellationToken);
        var neither = await CompressionCaptureRoutes.PreviewAsync(
            new CompressionPreviewRequest(null, null, null, null, null, null),
            new StubCaptureStore(),
            TestContext.Current.CancellationToken);

        Assert.IsType<BadRequest<ErrorResponse>>(both);
        Assert.IsType<BadRequest<ErrorResponse>>(neither);
    }

    /// <summary>
    /// The captureId form is bounded by what the proxy stored; the text form is bounded only by the
    /// request body limit, and the pipeline allocates scratch proportional to its input.
    /// </summary>
    [Fact]
    public async Task Preview_TextForm_RejectsOversizedText()
    {
        var result = await CompressionCaptureRoutes.PreviewAsync(
            new CompressionPreviewRequest(
                null, new string('x', 2_000_001), "Bash", null, "anthropic", null),
            new StubCaptureStore(),
            TestContext.Current.CancellationToken);

        Assert.IsType<BadRequest<ErrorResponse>>(result);
    }

    [Fact]
    public async Task Preview_TextForm_RequiresToolNameAndProvider()
    {
        var result = await CompressionCaptureRoutes.PreviewAsync(
            new CompressionPreviewRequest(null, "text", null, null, "anthropic", null),
            new StubCaptureStore(),
            TestContext.Current.CancellationToken);

        Assert.IsType<BadRequest<ErrorResponse>>(result);
    }

    [Fact]
    public async Task Preview_CaptureForm_StillReplaysTheStoredRawText()
    {
        var id = Guid.NewGuid();
        var store = new StubCaptureStore(new CompressionCaptureDetail(
            id, DateTime.UtcNow, "anthropic", "Bash", "echo value", "value  \n", "value  \n",
            8, 8, false, false, [], []));

        var result = await CompressionCaptureRoutes.PreviewAsync(
            new CompressionPreviewRequest(id, null, null, null, null,
                [CompressionCatalog.TrailingWhitespace, CompressionCatalog.ScopeShell]),
            store,
            TestContext.Current.CancellationToken);

        var ok = Assert.IsType<Ok<CompressionPreviewResponse>>(result);
        Assert.Equal("value\n", ok.Value!.Output);
        Assert.Equal(1, store.GetCalls);
    }

    [Fact]
    public async Task Preview_UnknownCaptureId_IsNotFound()
    {
        var result = await CompressionCaptureRoutes.PreviewAsync(
            new CompressionPreviewRequest(Guid.NewGuid(), null, null, null, null, null),
            new StubCaptureStore(),
            TestContext.Current.CancellationToken);

        Assert.IsType<NotFound<ErrorResponse>>(result);
    }

    private sealed class StubCaptureStore(CompressionCaptureDetail? seed = null) : ICompressionCaptureStore
    {
        public int GetCalls { get; private set; }

        public void Record(CompressionCapture capture) => throw new NotSupportedException();

        public Task<List<CompressionCaptureSummary>> ListAsync(
            int take, int skip, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<CompressionCaptureDetail?> GetAsync(Guid id, CancellationToken cancellationToken)
        {
            GetCalls++;
            return Task.FromResult(id == seed?.Id ? seed : null);
        }

        public Task<int> ClearAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
