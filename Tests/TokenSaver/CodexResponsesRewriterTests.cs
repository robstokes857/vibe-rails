using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using global::TokenSaver;
using TokenSaver.Minify;
using TokenSaver.Pipeline;
using Xunit;

namespace Tests.TokenSaver;

public class CodexResponsesRewriterTests
{
    private static readonly string CodexFixtureDir = GetCodexFixtureDir();

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
    public void Rewrite_NonShellFunctionOutput_CapturesButPassesThroughByteIdentical()
    {
        const string body = """
            {"input":[
              {"type":"function_call","name":"apply_patch","call_id":"c1","arguments":"{}"},
              {"type":"function_call_output","call_id":"c1","output":"\u001b[31mkeep me\u001b[0m  \n"}
            ]}
            """;
        var plan = CompressionCatalog.Resolve(null);
        var captures = new RecordingCaptureSink();

        var (result, rewritten, writtenCount) = RewriteIncludingNoOp(
            body, plan: plan, captures: captures);

        Assert.False(result.Rewritten);
        Assert.Equal(1, result.ToolResultsSeen);
        Assert.Equal(0, result.ToolResultsMinified);
        Assert.Equal(result.BytesBefore, result.BytesAfter);
        Assert.Equal(0, writtenCount);
        Assert.Equal(body, rewritten);

        var capture = Assert.Single(captures.Captures);
        Assert.Equal("openai", capture.Provider);
        Assert.Equal("apply_patch", capture.ToolName);
        Assert.Null(capture.Command);
        Assert.Equal("\u001b[31mkeep me\u001b[0m  \n", capture.RawText);
        Assert.Equal(capture.RawText, capture.CompressedText);
        Assert.False(capture.Changed);
        Assert.False(capture.RewriteAccepted);
        Assert.Empty(capture.Trace);
    }

    [Fact]
    public void Rewrite_CurrentCodeModeExec_CompressesTextAndLeavesBinaryBlocksByteVerbatim()
    {
        var body = File.ReadAllBytes(
            Path.Combine(CodexFixtureDir, "code_mode_exec_request.json"));
        var plan = CompressionCatalog.Resolve(null);
        var writer = new ArrayBufferWriter<byte>();
        var captures = new RecordingCaptureSink();

        // plan_1A A1 (2026-08-16): code-mode exec output is compressed, no longer just observed.
        Assert.Contains("exec", plan.CodexAllowlist);

        var result = CodexResponsesRewriter.Rewrite(body, plan, writer, captures);

        Assert.True(result.Rewritten);
        Assert.Equal(1, result.ToolResultsSeen);
        Assert.Equal(1, result.ToolResultsMinified);
        Assert.True(result.BytesAfter < result.BytesBefore);

        // The curated set keeps ansi-strip and cr-collapse OFF: the SGR bytes survive while
        // crlf-normalize, trailing-whitespace and blank-edges take the framing around them.
        var rewritten = Encoding.UTF8.GetString(writer.WrittenSpan);
        using var document = JsonDocument.Parse(rewritten);
        Assert.Equal(
            "\u001b[31mdiagnostic exec output\u001b[0m",
            document.RootElement.GetProperty("input")[1].GetProperty("output")[0]
                .GetProperty("text").GetString());
        Assert.Contains("NOT_CAPTURED_IMAGE", rewritten, StringComparison.Ordinal);
        Assert.Contains("NOT_CAPTURED_AUDIO", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"preserve\": \"byte-for-byte\"", rewritten, StringComparison.Ordinal);

        var capture = Assert.Single(captures.Captures);
        Assert.Equal("openai", capture.Provider);
        Assert.Equal("exec", capture.ToolName);
        Assert.Equal(
            "const result = await tools.shell_command({\"command\":\"Get-ChildItem\"});\ntext(result);",
            capture.Command);
        Assert.Equal("\u001b[31mdiagnostic exec output\u001b[0m  \r\n", capture.RawText);
        Assert.Equal("\u001b[31mdiagnostic exec output\u001b[0m", capture.CompressedText);
        Assert.True(capture.Changed);
        Assert.True(capture.RewriteAccepted);
        Assert.NotEmpty(capture.Trace);
        Assert.DoesNotContain("NOT_CAPTURED_IMAGE", capture.RawText, StringComparison.Ordinal);
        Assert.DoesNotContain("NOT_CAPTURED_AUDIO", capture.RawText, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_CurrentCodeModeExec_ImageOnlyOutput_IsNotCaptured()
    {
        const string body = """
            {"input":[
              {"type":"custom_tool_call","call_id":"c1","name":"exec","input":"image-only"},
              {"type":"custom_tool_call_output","call_id":"c1","output":[
                {"type":"input_image","image_url":"data:image/png;base64,NOT_CAPTURED_IMAGE"},
                {"type":"input_audio","audio_url":"data:audio/wav;base64,NOT_CAPTURED_AUDIO"}
              ]}
            ]}
            """;
        var plan = CompressionCatalog.Resolve(null);
        var captures = new RecordingCaptureSink();

        var (result, rewritten, writtenCount) = RewriteIncludingNoOp(
            body, plan: plan, captures: captures);

        Assert.False(result.Rewritten);
        Assert.Equal(1, result.ToolResultsSeen);
        Assert.Equal(0, result.ToolResultsMinified);
        Assert.Equal(result.BytesBefore, result.BytesAfter);
        Assert.Equal(0, writtenCount);
        Assert.Equal(body, rewritten);
        Assert.Empty(captures.Captures);
    }

    [Fact]
    public void Rewrite_CurrentCodeModeExec_MultipleTextBlocks_AreCompressedAndCapturedSeparately()
    {
        const string body = """
            {"input":[
              {"type":"custom_tool_call","call_id":"c1","name":"exec","input":"pwd"},
              {"type":"custom_tool_call_output","call_id":"c1","output":[
                {"type":"input_text","text":"first  \n"},
                {"type":"input_image","image_url":"data:image/png;base64,NOT_CAPTURED"},
                {"type":"input_text","text":"second  \n"}
              ]}
            ]}
            """;
        var plan = CompressionCatalog.Resolve(null);
        var captures = new RecordingCaptureSink();

        var (result, rewritten, _) = RewriteIncludingNoOp(
            body, plan: plan, captures: captures);

        Assert.True(result.Rewritten);
        Assert.Equal(1, result.ToolResultsSeen);
        Assert.Equal(1, result.ToolResultsMinified);
        Assert.Contains("\"text\":\"first\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("\"text\":\"second\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("data:image/png;base64,NOT_CAPTURED", rewritten, StringComparison.Ordinal);
        Assert.Collection(
            captures.Captures,
            capture =>
            {
                Assert.Equal("first  \n", capture.RawText);
                Assert.Equal("first", capture.CompressedText);
            },
            capture =>
            {
                Assert.Equal("second  \n", capture.RawText);
                Assert.Equal("second", capture.CompressedText);
            });
        Assert.All(captures.Captures, capture =>
        {
            Assert.Equal("exec", capture.ToolName);
            Assert.True(capture.RewriteAccepted);
            Assert.NotEmpty(capture.Trace);
        });
    }

    [Fact]
    public void Rewrite_DiagnosticCapture_WithNoCompressionWork_StillCapturesWithoutRewriting()
    {
        const string body = """
            {"input":[
              {"type":"custom_tool_call","call_id":"c1","name":"exec","input":"pwd"},
              {"type":"custom_tool_call_output","call_id":"c1","output":[
                {"type":"input_text","text":"diagnostic only  \n"}
              ]}
            ]}
            """;
        var plan = CompressionCatalog.Resolve([]);
        var captures = new RecordingCaptureSink();

        Assert.True(plan.IsNoOp);
        Assert.Empty(plan.CodexAllowlist);

        var (result, rewritten, writtenCount) = RewriteIncludingNoOp(
            body, plan: plan, captures: captures);

        Assert.False(result.Rewritten);
        Assert.Equal(1, result.ToolResultsSeen);
        Assert.Equal(0, writtenCount);
        Assert.Equal(body, rewritten);

        var capture = Assert.Single(captures.Captures);
        Assert.Equal("exec", capture.ToolName);
        Assert.Equal("pwd", capture.Command);
        Assert.Equal("diagnostic only  \n", capture.RawText);
        Assert.Equal(capture.RawText, capture.CompressedText);
        Assert.False(capture.RewriteAccepted);
        Assert.Empty(capture.Trace);
        Assert.Empty(capture.EnabledIds);
    }

    [Fact]
    public void Rewrite_MixedExecAndShellOutputs_RewritesBothThroughTheShellScope()
    {
        const string body = """
            {"input":[
              {"type":"custom_tool_call","call_id":"exec-1","name":"exec","input":"pwd"},
              {"type":"custom_tool_call_output","call_id":"exec-1","output":[
                {"type":"input_text","text":"exec \\u263a  \n"}
              ]},
              {"type":"function_call","name":"shell_command","call_id":"shell-1","arguments":"{\"command\":\"pwd\"}"},
              {"type":"function_call_output","call_id":"shell-1","output":"shell output  \n"}
            ],"metadata":{"keep":"exact"}}
            """;
        var plan = CompressionCatalog.Resolve([
            CompressionCatalog.TrailingWhitespace,
            CompressionCatalog.ScopeShell]);
        var captures = new RecordingCaptureSink();

        var (result, rewritten, _) = RewriteIncludingNoOp(
            body, plan: plan, captures: captures);

        Assert.True(result.Rewritten);
        Assert.Equal(2, result.ToolResultsSeen);
        Assert.Equal(2, result.ToolResultsMinified);
        Assert.Equal("exec \\u263a\n", ReadFirstOutputText(rewritten));
        Assert.Equal("shell output\n", ReadOutput(rewritten, 3));
        Assert.Contains("\"metadata\":{\"keep\":\"exact\"}", rewritten, StringComparison.Ordinal);

        Assert.Collection(
            captures.Captures,
            capture =>
            {
                Assert.Equal("exec", capture.ToolName);
                Assert.Equal("exec \\u263a  \n", capture.RawText);
                Assert.Equal("exec \\u263a\n", capture.CompressedText);
                Assert.True(capture.RewriteAccepted);
                Assert.NotEmpty(capture.Trace);
            },
            capture =>
            {
                Assert.Equal("shell_command", capture.ToolName);
                Assert.Equal("shell output  \n", capture.RawText);
                Assert.Equal("shell output\n", capture.CompressedText);
                Assert.True(capture.RewriteAccepted);
            });
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
        Assert.Equal("shell_command", capture.ToolName);
        Assert.Equal("git status --short", capture.Command);
        Assert.True(capture.Changed);
        Assert.NotEqual(capture.RawText, capture.CompressedText);
        Assert.Equal(ReadFirstOutput(rewritten), capture.CompressedText);
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

    private static string? ReadOutput(string body, int index)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("input")[index].GetProperty("output").GetString();
    }

    private static string? ReadFirstOutputText(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("input")[1].GetProperty("output")[0]
            .GetProperty("text").GetString();
    }

    private static string GetCodexFixtureDir([CallerFilePath] string? callerPath = null) =>
        Path.Combine(Path.GetDirectoryName(callerPath)!, "Fixtures", "Codex");

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
