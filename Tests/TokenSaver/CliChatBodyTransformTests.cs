using System.Text;
using Microsoft.AspNetCore.Http;
using TokenSaver;
using TokenSaver.Minify;
using TokenSaver.Pipeline;
using Xunit;

namespace Tests.TokenSaver;

/// <summary>
/// Native Grok speaks two body shapes behind one base URL (grok 1.0.5): grok-4.6 POSTs
/// /responses, grok-build POSTs /chat/completions. The composite must buffer both and
/// decline everything else, and the default plan must carry Grok's wire tool names.
/// </summary>
public sealed class CliChatBodyTransformTests
{
    [Fact]
    public void DefaultPlan_CarriesGroksWireToolNames()
    {
        var plan = CompressionCatalog.Resolve(null);

        Assert.Contains("run_terminal_command", plan.GrokAllowlist);
        Assert.Contains("get_command_or_subagent_output", plan.GrokAllowlist);
        // Read/Grep scopes are off by default across every provider.
        Assert.DoesNotContain("read_file", plan.GrokAllowlist);
        Assert.DoesNotContain("grep", plan.GrokAllowlist);
    }

    [Theory]
    [InlineData("/llm/cli-chat/v1/responses")]
    [InlineData("/llm/cli-chat/v1/chat/completions")]
    public async Task Post_ToEitherInferenceShape_IsBuffered(string path)
    {
        var transform = new CliChatBodyTransform(CompressionCatalog.Resolve(null));

        var transformed = await transform.TryTransformAsync(
            PostJson(path, "{\"model\":\"grok-4.6\",\"input\":[]}"),
            new NullEventSink(),
            TestContext.Current.CancellationToken);

        Assert.NotNull(transformed);
        transformed.BufferLease?.Dispose();
    }

    [Fact]
    public async Task NonInferenceTraffic_PassesThroughUntouched()
    {
        var transform = new CliChatBodyTransform(CompressionCatalog.Resolve(null));

        var models = await transform.TryTransformAsync(
            PostJson("/llm/cli-chat/v1/models", "{}"),
            new NullEventSink(),
            TestContext.Current.CancellationToken);
        Assert.Null(models);

        var get = PostJson("/llm/cli-chat/v1/responses", "{}");
        get.Method = HttpMethods.Get;
        Assert.Null(await transform.TryTransformAsync(
            get, new NullEventSink(), TestContext.Current.CancellationToken));
    }

    private static HttpRequest PostJson(string path, string body)
    {
        var context = new DefaultHttpContext();
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = path;
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        return context.Request;
    }

    private sealed class NullEventSink : ILlmProxyEventSink
    {
        public void ProxyActivity(
            string source, string? label, string? target, string? status, long? bytesSaved = null)
        {
        }

        public void SavingsMeasured(LlmProxySavingsReport report)
        {
        }

        public void Diagnostic(string source, string message, Exception? exception = null)
        {
        }
    }
}
