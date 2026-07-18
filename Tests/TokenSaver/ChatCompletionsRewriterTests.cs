using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using TokenSaver;
using TokenSaver.Minify;
using TokenSaver.Pipeline;
using Xunit;

namespace Tests.TokenSaver;

public sealed class ChatCompletionsRewriterTests
{
    private const string DirtyOutput = "\u001b[1mhi\u001b[0m  \r\ndone";
    private static readonly CompressionPlan FullLosslessPlan = CompressionCatalog.Resolve([
        CompressionCatalog.CrCollapse,
        CompressionCatalog.AnsiStrip,
        CompressionCatalog.TrailingWhitespace,
        CompressionCatalog.BlankEdges,
        CompressionCatalog.BlankRuns,
        CompressionCatalog.ScopeShell]);

    [Fact]
    public void Rewrite_ChangesOnlyMatchedAllowlistedToolMessages()
    {
        var body = BuildBody(
            ("call_1", "bash", "printf hi", DirtyOutput),
            ("call_2", "read", "README.md", "leave me  \n"));
        var input = Encoding.UTF8.GetBytes(body);
        var output = new ArrayBufferWriter<byte>();
        var captures = new RecordingCaptureSink();

        var result = ChatCompletionsRewriter.Rewrite(
            input,
            FullLosslessPlan,
            output,
            captures);

        Assert.True(result.Rewritten);
        Assert.Equal(2, result.ToolResultsSeen);
        Assert.Equal(1, result.ToolResultsMinified);
        using var rewritten = JsonDocument.Parse(output.WrittenMemory);
        var messages = rewritten.RootElement.GetProperty("messages");
        Assert.Equal("system text  \n", messages[0].GetProperty("content").GetString());
        Assert.Equal("hi\ndone", messages[2].GetProperty("content").GetString());
        Assert.Equal("leave me  \n", messages[4].GetProperty("content").GetString());

        var capture = Assert.Single(captures.Captures);
        Assert.Equal("zai", capture.Provider);
        Assert.Equal("bash", capture.ToolName);
        Assert.Equal("printf hi", capture.Command);
        Assert.True(capture.RewriteAccepted);
    }

    [Fact]
    public void Rewrite_MalformedJsonFailsOpenWithoutProducingReplacementBytes()
    {
        var input = Encoding.UTF8.GetBytes("{\"messages\":[{\"role\":");
        var output = new ArrayBufferWriter<byte>();

        var result = ChatCompletionsRewriter.Rewrite(
            input,
            FullLosslessPlan,
            output);

        Assert.False(result.Rewritten);
        Assert.Equal(input.Length, result.BytesAfter);
        Assert.Equal(0, output.WrittenCount);
    }

    [Fact]
    public async Task ZaiBodyTransform_RewritesQualifyingChatCompletionsRequest()
    {
        var body = BuildBody(("call_1", "bash", "printf hi", DirtyOutput));
        var bytes = Encoding.UTF8.GetBytes(body);
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Post;
        context.Request.Path = "/llm/zai/api/paas/v4/chat/completions";
        context.Request.ContentType = "application/json";
        context.Request.ContentLength = bytes.Length;
        context.Request.Body = new MemoryStream(bytes);
        var transform = new ZaiBodyTransform(FullLosslessPlan);

        var transformed = await transform.TryTransformAsync(
            context.Request,
            NullEventSink.Instance,
            TestContext.Current.CancellationToken);

        Assert.NotNull(transformed);
        try
        {
            Assert.True(transformed.Savings?.Rewritten);
            var forwarded = await transformed.Content.ReadAsStringAsync(
                TestContext.Current.CancellationToken);
            Assert.Contains("hi\\ndone", forwarded, StringComparison.Ordinal);
            Assert.DoesNotContain("u001b", forwarded, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            transformed.BufferLease?.Dispose();
            transformed.Content.Dispose();
        }
    }

    [Theory]
    [InlineData("GET", "/llm/zai/api/paas/v4/chat/completions", "application/json")]
    [InlineData("POST", "/llm/zai/api/paas/v4/models", "application/json")]
    [InlineData("POST", "/llm/zai/api/paas/v4/chat/completions", "text/plain")]
    public async Task ZaiBodyTransform_DeclinesNonQualifyingRequests(
        string method,
        string path,
        string contentType)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.ContentType = contentType;
        context.Request.ContentLength = 2;
        context.Request.Body = new MemoryStream("{}"u8.ToArray());
        var transform = new ZaiBodyTransform(FullLosslessPlan);

        var transformed = await transform.TryTransformAsync(
            context.Request,
            NullEventSink.Instance,
            TestContext.Current.CancellationToken);

        Assert.Null(transformed);
    }

    private static string BuildBody(params (string Id, string Name, string Command, string Output)[] calls)
    {
        var messages = new List<object>
        {
            new { role = "system", content = "system text  \n" }
        };
        foreach (var call in calls)
        {
            messages.Add(new
            {
                role = "assistant",
                content = (string?)null,
                tool_calls = new[]
                {
                    new
                    {
                        id = call.Id,
                        type = "function",
                        function = new
                        {
                            name = call.Name,
                            arguments = JsonSerializer.Serialize(new { command = call.Command })
                        }
                    }
                }
            });
            messages.Add(new
            {
                role = "tool",
                tool_call_id = call.Id,
                content = call.Output
            });
        }

        return JsonSerializer.Serialize(new { model = "glm-5.2", messages });
    }

    private sealed class RecordingCaptureSink : ICompressionCaptureSink
    {
        public List<CompressionCapture> Captures { get; } = [];

        public void Capture(CompressionCapture capture) => Captures.Add(capture);
    }

    private sealed class NullEventSink : ILlmProxyEventSink
    {
        public static readonly NullEventSink Instance = new();

        public void ProxyActivity(
            string source,
            string? label,
            string? target,
            string? status,
            long? bytesSaved = null)
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
