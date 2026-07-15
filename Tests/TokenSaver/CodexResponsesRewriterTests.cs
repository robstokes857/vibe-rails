using System.Buffers;
using System.Text;
using System.Text.Json;
using TokenSaver.Minify;
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
    public void Rewrite_ProviderShellOutput_MinifiesStdoutAndStderr()
    {
        const string body = """
            {"input":[{"output":[
              {"stdout":"ok  \n","stderr":"\u001b[31mwarning\u001b[0m\n","outcome":{"type":"exit","exit_code":0}}
            ],"call_id":"c1","type":"shell_call_output"}]}
            """;

        var (result, rewritten) = Rewrite(body, allowlist: []);
        using var document = JsonDocument.Parse(rewritten);
        var chunk = document.RootElement.GetProperty("input")[0].GetProperty("output")[0];

        Assert.True(result.Rewritten);
        Assert.Equal("ok", chunk.GetProperty("stdout").GetString());
        Assert.Equal("warning", chunk.GetProperty("stderr").GetString());
    }

    [Fact]
    public void Rewrite_HighTierCondensesAndIsStableAcrossTurns()
    {
        var spam = string.Join("\\n", Enumerable.Repeat("retrying operation", 40));
        var body = $$$"""
            {"input":[
              {"type":"function_call","name":"shell_command","call_id":"c1","arguments":"{}"},
              {"type":"function_call_output","call_id":"c1","output":"{{{spam}}}"}
            ]}
            """;
        var (flags, condense, _) = TokenSaverPresets.For(TokenSaverLevel.High);
        var allowlist = CodexResponsesRewriter.DefaultToolAllowlist;

        var (first, firstBody) = Rewrite(body, flags, condense, allowlist);
        var (again, againBody) = Rewrite(firstBody, flags, condense, allowlist);

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
        IReadOnlyCollection<string>? allowlist = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var writer = new ArrayBufferWriter<byte>();
        var result = CodexResponsesRewriter.Rewrite(
            bytes,
            flags ?? MinifyFlags.Default,
            condense,
            allowlist ?? CodexResponsesRewriter.DefaultToolAllowlist,
            writer);
        return (
            result,
            result.Rewritten ? Encoding.UTF8.GetString(writer.WrittenSpan) : body,
            writer.WrittenCount);
    }
}
