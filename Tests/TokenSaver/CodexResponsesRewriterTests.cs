using System.Buffers;
using System.Text;
using System.Text.Json;
using global::TokenSaver;
using TokenSaver.Minify;
using TokenSaver.Pipeline;
using Xunit;

namespace Tests.TokenSaver;

public class CodexResponsesRewriterTests
{
    [Fact]
    public void Rewrite_ShellFunctionOutput_MinifiesAndPreservesUnrelatedBytes()
    {
        const string body = """
            {"model":"gpt-5.5-codex","input":[
              {"arguments":"{\"command\":\"Get-ChildItem\"}","call_id":"c1","name":"shell_command","type":"function_call"},
              {"output":"\u001b[31mred\u001b[0m  \r\n","call_id":"c1","type":"function_call_output"},
              {"type":"message","role":"user","content":[{"type":"input_text","text":"keep  exactly"}]}
            ],"metadata":{"keep":"byte-for-byte"}}
            """;

        var (result, rewritten) = Rewrite(body);

        Assert.True(result.Rewritten);
        Assert.Equal(1, result.ToolResultsSeen);
        Assert.Equal(1, result.ToolResultsMinified);
        Assert.Equal("red", ReadFirstOutput(rewritten));
        Assert.Contains("\"text\":\"keep  exactly\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"metadata\":{\"keep\":\"byte-for-byte\"}", rewritten, StringComparison.Ordinal);
        Assert.True(result.BytesAfter < result.BytesBefore);
    }

    [Fact]
    public void Rewrite_NonShellFunctionOutput_PassesThroughByteIdentical()
    {
        const string body = """
            {"input":[
              {"type":"function_call","name":"apply_patch","call_id":"c1","arguments":"{}"},
              {"type":"function_call_output","call_id":"c1","output":"\u001b[31mkeep me\u001b[0m  \n"}
            ]}
            """;

        var (result, rewritten, writtenCount) = RewriteIncludingNoOp(body);

        Assert.False(result.Rewritten);
        Assert.Equal(1, result.ToolResultsSeen);
        Assert.Equal(0, writtenCount);
        Assert.Equal(body, rewritten);
    }

    [Fact]
    public void Rewrite_CustomToolArrayOutput_UsesCallIdCorrelation()
    {
        const string body = """
            {"input":[
              {"type":"custom_tool_call","call_id":"c1","name":"exec_command","input":"pwd"},
              {"type":"custom_tool_call_output","call_id":"c1","output":[
                {"type":"input_text","text":"line  \n"},
                {"type":"input_image","image_url":"data:image/png;base64,abc"}
              ]}
            ]}
            """;

        var (result, rewritten) = Rewrite(body);

        Assert.True(result.Rewritten);
        Assert.Equal("line", ReadFirstOutputText(rewritten));
        Assert.Contains("data:image/png;base64,abc", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_ProviderShellOutput_RequiresShellScope()
    {
        const string body = """
            {"input":[{"output":[
              {"stdout":"ok  \n","stderr":"\u001b[31mwarning\u001b[0m\n","outcome":{"type":"exit","exit_code":0}}
            ],"call_id":"c1","type":"shell_call_output"}]}
            """;

        var (disabled, untouched, writtenCount) = RewriteIncludingNoOp(body, allowlist: []);
        Assert.False(disabled.Rewritten);
        Assert.Equal(0, writtenCount);
        Assert.Equal(body, untouched);

        var (result, rewritten) = Rewrite(body, allowlist: ["shell_command"]);
        using var document = JsonDocument.Parse(rewritten);
        var chunk = document.RootElement.GetProperty("input")[0].GetProperty("output")[0];

        Assert.True(result.Rewritten);
        Assert.Equal("ok", chunk.GetProperty("stdout").GetString());
        Assert.Equal("warning", chunk.GetProperty("stderr").GetString());
    }

    [Fact]
    public void Rewrite_CatalogPlan_AppliesShapeFilterAndCapturesCommand()
    {
        const string body = """
            {"input":[
              {"type":"function_call","name":"shell_command","call_id":"c1","arguments":"{\"command\":\"git status --short\"}"},
              {"type":"function_call_output","call_id":"c1","output":" M src/a.cs\n M src/b.cs\n M src/c.cs\n M src/d.cs\n M src/e.cs\n M src/f.cs\n M src/g.cs\n M src/h.cs\n"}
            ]}
            """;
        var plan = CompressionCatalog.Resolve([
            CompressionCatalog.GitStatusGroup,
            CompressionCatalog.ScopeShell]);
        var captures = new RecordingCaptureSink();

        var (_, rewritten, _) = RewriteIncludingNoOp(
            body, plan: plan, allowlist: plan.CodexAllowlist, captures: captures);

        Assert.Contains(" M:\n", ReadFirstOutput(rewritten), StringComparison.Ordinal);
        var capture = Assert.Single(captures.Captures);
        Assert.Equal("openai", capture.Provider);
        Assert.Equal("git status --short", capture.Command);
        Assert.True(capture.RewriteAccepted);
        Assert.Contains(capture.Trace, trace =>
            trace.StageId == CompressionCatalog.GitStatusGroup
            && trace.Outcome == StageOutcome.Applied);
    }

    [Fact]
    public void Rewrite_AllStagesCondenseAndAreStableAcrossTurns()
    {
        var spam = string.Join("\\n", Enumerable.Repeat("retrying operation", 40));
        var body = $$$"""
            {"input":[
              {"type":"function_call","name":"shell_command","call_id":"c1","arguments":"{}"},
              {"type":"function_call_output","call_id":"c1","output":"{{{spam}}}"}
            ]}
            """;
        // Every stage the condenser needs to fire: the lossless pass plus both lossy ones.
        var plan = CompressionCatalog.Resolve([
            CompressionCatalog.CrCollapse, CompressionCatalog.AnsiStrip,
            CompressionCatalog.TrailingWhitespace, CompressionCatalog.BlankEdges,
            CompressionCatalog.BlankRuns, CompressionCatalog.DedupeLines,
            CompressionCatalog.TruncateLong]);
        var allowlist = CodexResponsesRewriter.DefaultToolAllowlist;

        var (first, firstBody) = Rewrite(body, plan.Flags, plan.Condense, allowlist);
        var (again, againBody) = Rewrite(firstBody, plan.Flags, plan.Condense, allowlist);

        Assert.True(first.Rewritten,
            $"seen={first.ToolResultsSeen}, minified={first.ToolResultsMinified}, body={body}");
        Assert.Contains("retrying operation [x40]", ReadFirstOutput(firstBody), StringComparison.Ordinal);
        Assert.False(again.Rewritten);
        Assert.Equal(firstBody, againBody);
    }

    [Fact]
    public void Rewrite_MalformedJson_FailsOpen()
    {
        const string body = "{\"input\":[{\"type\":\"function_call_output\"";

        var (result, rewritten, writtenCount) = RewriteIncludingNoOp(body);

        Assert.False(result.Rewritten);
        Assert.Equal(0, writtenCount);
        Assert.Equal(body, rewritten);
    }

    private static string? ReadFirstOutput(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("input")[1].GetProperty("output").GetString();
    }

    private static string? ReadFirstOutputText(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("input")[1].GetProperty("output")[0]
            .GetProperty("text").GetString();
    }

    private static (ToolOutputRewriteResult Result, string Output) Rewrite(
        string body,
        MinifyFlags? flags = null,
        CondenseOptions condense = default,
        IReadOnlyCollection<string>? allowlist = null)
    {
        var (result, output, _) = RewriteIncludingNoOp(body, flags, condense, allowlist);
        return (result, output);
    }

    private static (ToolOutputRewriteResult Result, string Output, int WrittenCount) RewriteIncludingNoOp(
        string body,
        MinifyFlags? flags = null,
        CondenseOptions condense = default,
        IReadOnlyCollection<string>? allowlist = null,
        CompressionPlan? plan = null,
        ICompressionCaptureSink? captures = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var writer = new ArrayBufferWriter<byte>();
        var result = plan is null
            ? CodexResponsesRewriter.Rewrite(
                bytes,
                flags ?? MinifyFlags.Default,
                condense,
                allowlist ?? CodexResponsesRewriter.DefaultToolAllowlist,
                writer)
            : CodexResponsesRewriter.Rewrite(bytes, plan, writer, captures);
        return (
            result,
            result.Rewritten ? Encoding.UTF8.GetString(writer.WrittenSpan) : body,
            writer.WrittenCount);
    }

    private sealed class RecordingCaptureSink : ICompressionCaptureSink
    {
        public List<CompressionCapture> Captures { get; } = [];
        public void Capture(CompressionCapture capture) => Captures.Add(capture);
    }
}
