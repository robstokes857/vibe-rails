using System.Buffers;
using System.Text;
using System.Text.Json;
using TokenSaver;
using TokenSaver.Minify;
using TokenSaver.Pipeline;
using TokenSaver.Shape;
using Xunit;

namespace Tests.TokenSaver;

public class CodexTestElisionTests
{
    private const string Passes = "✔ a sufficiently long passing test (1ms)\n✔ another sufficiently long passing test (2ms)\n";
    private const string Failure = "✖ broken test (1ms)\nError: expected 1, got 2\nℹ pass 2\nℹ fail 1\n";
    private static readonly CompressionPlan ElideOnly = CompressionCatalog.Resolve([
        CompressionCatalog.ScopeShell, CompressionCatalog.ElidePassedTests, CompressionCatalog.CrLfNormalize]);

    [Theory]
    [InlineData("text(await tools.exec_command({cmd: 'node --test tests/*.test.mjs'}));")]
    [InlineData("// @exec: {\"max_output_tokens\":1000}\nconst r = await tools.exec_command({cmd: \"dotnet test\", yield_time_ms: 1000}); text(r);")]
    [InlineData("let r=await tools.shell_command({command:'pytest -v'});text(JSON.stringify(r));")]
    [InlineData("var r=await tools.exec_command({cmd:`node --test`,env:{CI:'1'},login:false});text(r);")]
    [InlineData("text(await tools.exec_command({\"cmd\": \"node --test --test-name-pattern=\\\"two words\\\"\"}));")]
    [InlineData("const r=await Promise.all([tools.exec_command({cmd:'node --test'}),tools.exec_command({cmd:'dotnet test'})]);text(r);")]
    [InlineData("const r=await Promise.allSettled([tools.exec_command({cmd:'node --test'}),tools.exec_command({cmd:'dotnet test'})]);r.forEach(text);")]
    [InlineData("const r=await Promise.all([tools.exec_command({cmd:'node --test'})]);r.forEach(x => text(x));")]
    [InlineData("const r=await Promise.all([tools.exec_command({cmd:'node --test'})]);r.forEach((x) => text(x));")]
    [InlineData("text(await tools.exec_command({cmd:'node --test'}));text(await tools.exec_command({cmd:'cargo test'}));")]
    [InlineData("const results=await Promise.all([tools.exec_command({cmd:'node --test'})]);for(const r of results){text(JSON.stringify({output:r.output,exit_code:r.exit_code,session_id:r.session_id}));}")]
    [InlineData("const r=await tools.exec_command({cmd:'node --test'});text(r);if(r.session_id) text(`SESSION_ID=${r.session_id}`);")]
    public void RecognizesCompleteLiteralPrograms(string code) =>
        Assert.Equal(TestOutputKind.Envelope, CodexTestCommands.Classify(code));

    [Theory]
    [InlineData("text(await tools.exec_command({cmd: command}));")]
    [InlineData("text(await tools.exec_command({cmd: `node --test ${file}`}));")]
    [InlineData("text(await tools.exec_command({cmd: 'node ' + '--test'}));")]
    [InlineData("text(await tools.exec_command({cmd:'node --test',...options}));")]
    [InlineData("text(await tools.exec_command({cmd:'node --test',cmd:'cat secret'}));")]
    [InlineData("text(await tools.exec_command({cmd:'node --test',env:getEnv()}));")]
    [InlineData("text(await tools.exec_command({cmd:'node --test',__proto__:{cmd:'cat secret'}}));")]
    [InlineData("const r=await tools.exec_command({cmd:'node --test'}); r.output='unrelated'; text(r);")]
    [InlineData("const r=await tools.exec_command({cmd:'node --test'}); text(r); text('unrelated');")]
    [InlineData("const text=await tools.exec_command({cmd:'node --test'}); text(text);")]
    [InlineData("const r=await Promise.all([tools.exec_command({cmd:'node --test'}),tools.exec_command({cmd:'cat secret'})]);text(r);")]
    [InlineData("const r=await Promise.all([tools.exec_command({cmd:'node --test'})]);r.forEach(x => text(x.output.replace('a','b')));")]
    [InlineData("text(await tools.exec_command({cmd:'node --test'}));text(await tools.read_file({path:'secret'}));")]
    [InlineData("/* text(await tools.exec_command({cmd:'node --test'})); */ text('fake');")]
    [InlineData("const s = \"text(await tools.exec_command({cmd:'node --test'}));\"; text(s);")]
    [InlineData("const r=await tools.exec_command({cmd:'node --test'});text(r);if(r.session_id) text(`SESSION_ID=${other.output}`);")]
    [InlineData("const r=await tools.exec_command({cmd:'node --test'});text(JSON.stringify({output:other.output}));")]
    [InlineData("const r=await tools.exec_command({cmd:'node --test'});text(JSON.stringify({output:r.output,output:r.exit_code}));")]
    public void AmbiguousProgramsDecline(string code) =>
        Assert.Equal(TestOutputKind.None, CodexTestCommands.Classify(code));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectExecCommandUsesCmdAndPreservesRequestBytes(bool envelope)
    {
        var output = envelope ? Envelope(Passes + Failure) : Passes + Failure;
        var body = Request("exec_command", "{\"cmd\":\"node --test\"}", output, custom: false);
        var (result, after) = Rewrite(body);
        Assert.True(result.Rewritten);
        Assert.Contains("[... 2 passed ...]", Output(after));
        Assert.Contains(Failure, envelope ? Unwrap(Output(after)) : Output(after));
        Assert.Equal(body.Replace(JsonSerializer.Serialize(output), JsonSerializer.Serialize(Output(after))), CanonicalStrings(after));
        AssertStable(after);
    }

    [Theory]
    [InlineData("text(await tools.exec_command({cmd:'node --test'}));")]
    [InlineData("const r=await tools.exec_command({cmd:'node --test'});text(r);")]
    [InlineData("const r=await tools.shell_command({command:'node --test'});text(JSON.stringify(r));")]
    public void CodeModeRewritesOnlyEnvelopeOutputAndKeepsFailureAndMetadata(string code)
    {
        var original = Envelope((Passes + Failure).Replace("\n", "\r\n"));
        var body = Request("exec", code, original);
        var captures = new CaptureSink();
        var (result, after) = Rewrite(body, captures: captures);
        Assert.True(result.Rewritten);
        var envelope = Output(after);
        Assert.Equal("[... 2 passed ...]\n" + Failure, Unwrap(envelope));
        Assert.Contains("\"wall_time_seconds\": 1.2500", envelope);
        Assert.Contains("\"exit_code\": 1", envelope);
        Assert.Contains("\"chunk_id\": \"keep\\u0020exactly\"", envelope);
        Assert.Contains("\"session_id\": 456", envelope);
        var capture = Assert.Single(captures.Items);
        Assert.Equal(code, capture.Command);
        Assert.Equal(original, capture.RawText);
        Assert.Equal(envelope, capture.CompressedText);
        Assert.True(capture.RewriteAccepted);
        Assert.Single(capture.Trace, x => x.StageId == CompressionCatalog.ElidePassedTests && x.Outcome == StageOutcome.Applied);
        Assert.Equal(capture.Trace.Count, capture.Trace.Select(x => x.StageId).Distinct().Count());
        AssertStable(after);
    }

    [Fact]
    public void ExplicitOutputFieldSupportsPlainText()
    {
        const string code = "const r=await tools.exec_command({cmd:'node --test'});text(r.output);";
        Assert.Equal(TestOutputKind.Text, CodexTestCommands.Classify(code));
        var (_, after) = Rewrite(Request("exec", code, Passes + Failure));
        Assert.Equal("[... 2 passed ...]\n" + Failure, Output(after));
        AssertStable(after);
    }

    [Fact]
    public void RealSessionMetadataSuffixLeavesStatusTextIntact()
    {
        const string code = "const r=await tools.exec_command({cmd:'node --test'});text(r.output); if(r.session_id) text(`SESSION_ID=${r.session_id}`);";
        var (_, after) = Rewrite(Request("exec", code, Passes + Failure + "SESSION_ID=123"));
        Assert.Equal("[... 2 passed ...]\n" + Failure + "SESSION_ID=123", Output(after));
        AssertStable(after);
    }

    [Fact]
    public void HomogeneousBatchRewritesEachResultWithoutRecountingMarkers()
    {
        const string code = "const r=await Promise.allSettled([tools.exec_command({cmd:'node --test'}),tools.exec_command({cmd:'pytest -v'})]);text(r);";
        var envelope = "[{\"status\":\"fulfilled\",\"value\":" + Envelope(Passes + Failure)
            + "},{\"status\":\"fulfilled\",\"value\":" + Envelope(Passes) + "}]";
        var captures = new CaptureSink();
        var (_, after) = Rewrite(Request("exec", code, envelope), captures: captures);
        using var parsed = JsonDocument.Parse(Output(after));
        Assert.Equal("[... 2 passed ...]\n" + Failure, parsed.RootElement[0].GetProperty("value").GetProperty("output").GetString());
        Assert.Equal("[... 2 passed ...]\n", parsed.RootElement[1].GetProperty("value").GetProperty("output").GetString());
        Assert.Single(Assert.Single(captures.Items).Trace, t => t.StageId == CompressionCatalog.ElidePassedTests);
        AssertStable(after);
    }

    [Theory]
    [InlineData("{\"output\":\"fake\",\"output\":$OUTPUT}")]
    [InlineData("{\"nested\":{\"output\":$OUTPUT}}")]
    [InlineData("{\"output\":$OUTPUT,\"unknown_metadata\":123}")]
    [InlineData("{\"output\":$OUTPUT")]
    [InlineData("[{\"output\":$OUTPUT},{\"unrelated\":\"value\"}]")]
    public void InvalidOrUnknownEnvelopesStayUntouched(string template)
    {
        var envelope = template.Replace("$OUTPUT", JsonSerializer.Serialize(Passes));
        var body = Request("exec", "text(await tools.exec_command({cmd:'node --test'}));", envelope);
        Assert.Equal(body, Rewrite(body).Body);
    }

    [Fact]
    public void MixedBatchAndUncorrelatedResultsNeverGainTestElision()
    {
        const string code = "const r=await Promise.all([tools.exec_command({cmd:'node --test'}),tools.exec_command({cmd:'cat source'})]);text(r);";
        var body = Request("exec", code, Envelope(Passes));
        Assert.Equal(body, Rewrite(body).Body);
        body = Request("exec", "text(await tools.exec_command({cmd:'node --test'}));", Envelope(Passes))
            .Replace("\"call_id\":\"call-1\",\"output\"", "\"call_id\":\"unmatched\",\"output\"");
        Assert.Equal(body, Rewrite(body).Body);
    }

    [Fact]
    public void ScopeAndStageOptOutAreRespected()
    {
        var body = Request("exec", "text(await tools.exec_command({cmd:'node --test'}));", Envelope(Passes));
        Assert.Equal(body, Rewrite(body, CompressionCatalog.Resolve([CompressionCatalog.ScopeShell])).Body);
        Assert.Equal(body, Rewrite(body, CompressionCatalog.Resolve([CompressionCatalog.ElidePassedTests])).Body);
    }

    [Theory]
    [InlineData("{\"command\":\"node --test\"}")]
    [InlineData("{\"cmd\":\"node --test\",\"cmd\":\"cat source\"}")]
    [InlineData("{\"cmd\":\"node --test\",\"code\":\"other\"}")]
    public void DirectCallsRequireOneMatchingArgument(string arguments)
    {
        var body = Request("exec_command", arguments, Passes, custom: false);
        Assert.Equal(body, Rewrite(body).Body);
    }

    [Fact]
    public void DuplicateCallIdsAreNotTrusted()
    {
        var body = Request("exec", "text(await tools.exec_command({cmd:'node --test'}));", Envelope(Passes));
        body = body.Replace("\"input\":[", "\"input\":[{\"type\":\"custom_tool_call\",\"call_id\":\"call-1\",\"name\":\"exec\",\"input\":\"text(await tools.exec_command({cmd:'cat source'}));\"},");
        Assert.Equal(body, Rewrite(body).Body);
    }

    [Fact]
    public void StructuredOutputRemainsStableAcrossStageCombinations()
    {
        string[] extras = [CompressionCatalog.AnsiStrip, CompressionCatalog.DedupeLines, CompressionCatalog.TruncateLong];
        for (var mask = 0; mask < 8; mask++)
        {
            var ids = new List<string> { CompressionCatalog.ScopeShell, CompressionCatalog.ElidePassedTests, CompressionCatalog.CrLfNormalize };
            for (var i = 0; i < extras.Length; i++) if ((mask & (1 << i)) != 0) ids.Add(extras[i]);
            var plan = CompressionCatalog.Resolve(ids);
            foreach (var text in new[] { Passes + Failure, "\u001b[32m" + Passes + "\u001b[0m", "[... 2 passed ...]\n" + Passes,
                string.Concat(Enumerable.Repeat(Passes + "log\nlog\nlog\n", 120)) + Failure })
            {
                var body = Request("exec", "text(await tools.exec_command({cmd:'node --test'}));", Envelope(text));
                var (_, once) = Rewrite(body, plan);
                Assert.Equal(once, Rewrite(once, plan).Body);
            }
        }
    }

    [Fact]
    public void InnerWireGrowthKeepsThatTokenAndDoesNotCountRejectedSavings()
    {
        var envelope = "[{\"output\":\"😀  \"}," + Envelope(Passes) + "]";
        const string code = "const r=await Promise.all([tools.exec_command({cmd:'node --test'}),tools.exec_command({cmd:'node --test'})]);text(r);";
        var captures = new CaptureSink();
        var (_, after) = Rewrite(Request("exec", code, envelope), CompressionCatalog.Resolve(null), captures);
        Assert.Contains("\"output\":\"😀  \"", Output(after));
        Assert.Contains("[... 2 passed ...]", Output(after));
        Assert.DoesNotContain(Assert.Single(captures.Items).Trace,
            t => t.StageId == CompressionCatalog.TrailingWhitespace && t.CharsRemoved > 0);
        Assert.Equal(after, Rewrite(after, CompressionCatalog.Resolve(null)).Body);
    }

    [Fact]
    public void CuratedDefaultsProduceStableSavingsThroughBothProviders()
    {
        var plan = CompressionCatalog.Resolve(null);
        var code = "text(await tools.exec_command({cmd:'node --test'}));";
        var body = Request("exec", code, Envelope(Passes + Failure));
        var (result, after) = Rewrite(body, plan);
        Assert.True(result.Rewritten);
        Assert.Equal(after, Rewrite(after, plan).Body);
        var claude = "{\"messages\":[{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"id\":\"call-1\",\"name\":\"Bash\",\"input\":{\"command\":\"cd /work && node --test 2>&1 | tail -20\"}}]},{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"call-1\",\"content\":" + JsonSerializer.Serialize(Passes + Failure) + "}]}]}";
        var writer = new ArrayBufferWriter<byte>();
        var rewritten = AnthropicMessagesRewriter.Rewrite(Encoding.UTF8.GetBytes(claude), plan, writer);
        Assert.True(rewritten.Rewritten);
        Assert.Contains("[... 2 passed ...]", Encoding.UTF8.GetString(writer.WrittenSpan));
    }

    private static string Envelope(string text) => "{\"chunk_id\": \"keep\\u0020exactly\", \"wall_time_seconds\": 1.2500, \"exit_code\": 1, \"session_id\": 456, \"output\": " + JsonSerializer.Serialize(text) + "}";
    private static string Unwrap(string envelope) { using var doc = JsonDocument.Parse(envelope); return doc.RootElement.GetProperty("output").GetString()!; }
    private static string Request(string name, string command, string output, bool custom = true) =>
        "{\"model\":\"test-model\",\"input\":[{\"type\":\"" + (custom ? "custom_tool_call" : "function_call") + "\",\"call_id\":\"call-1\",\"name\":" + JsonSerializer.Serialize(name)
        + ",\"" + (custom ? "input" : "arguments") + "\":" + JsonSerializer.Serialize(command)
        + "},{\"type\":\"" + (custom ? "custom_tool_call_output" : "function_call_output") + "\",\"call_id\":\"call-1\",\"output\":" + JsonSerializer.Serialize(output) + "}],\"metadata\": {\"keep\": 1.000}}";
    private static string Output(string body) { using var doc = JsonDocument.Parse(body); return doc.RootElement.GetProperty("input")[1].GetProperty("output").GetString()!; }
    private static string CanonicalStrings(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var token = doc.RootElement.GetProperty("input")[1].GetProperty("output").GetRawText();
        return body.Replace(token, JsonSerializer.Serialize(Output(body)));
    }
    private static (ToolOutputRewriteResult Result, string Body) Rewrite(string body, CompressionPlan? plan = null, CaptureSink? captures = null)
    {
        var writer = new ArrayBufferWriter<byte>();
        var result = CodexResponsesRewriter.Rewrite(Encoding.UTF8.GetBytes(body), plan ?? ElideOnly, writer, captures);
        var after = result.Rewritten ? Encoding.UTF8.GetString(writer.WrittenSpan) : body;
        Assert.True(Encoding.UTF8.GetByteCount(after) <= Encoding.UTF8.GetByteCount(body));
        return (result, after);
    }
    private static void AssertStable(string body) => Assert.Equal(body, Rewrite(body).Body);
    private sealed class CaptureSink : ICompressionCaptureSink
    {
        internal List<CompressionCapture> Items { get; } = [];
        public void Capture(CompressionCapture capture) => Items.Add(capture);
    }
}
