using System.Text.Json;
using VibeRails.Data.Sqlite.Replay;
using Xunit;

namespace Tests.DB;

public sealed class ReplayToolParserTests
{
    private const string GoodEvent = """{"item":{"type":"function_call","call_id":"good","name":"read_file","arguments":"ok"}}""";
    private static string Sse(params string[] events) => string.Join("\n\n", events.Select(e => "data: " + e)) + "\n\n";

    [Theory]
    [InlineData("""{"type":"content_block_start","content_block":{"type":"tool_use","name":"x"}}""")]
    [InlineData("""{"type":"content_block_start","index":"0","content_block":{"type":"tool_use","name":"x"}}""")]
    [InlineData("""{"type":"content_block_start","index":2147483648,"content_block":{"type":"tool_use","name":"x"}}""")]
    [InlineData("""{"type":"content_block_start","index":-1,"content_block":{"type":"tool_use","name":"x"}}""")]
    [InlineData("""{"type":"content_block_start","index":0,"content_block":[]} """)]
    [InlineData("""{"type":"content_block_delta","index":{},"delta":{"partial_json":"x"}}""")]
    [InlineData("""{"type":"content_block_delta","index":0.5,"delta":null}""")]
    [InlineData("""{"choices":[null,7,{"message":{"tool_calls":{}}}]}""")]
    [InlineData("""{"choices":[{"delta":{"tool_calls":[{"index":false,"function":{"name":"x"}}]}}]}""")]
    [InlineData("""{"choices":[{"delta":{"tool_calls":[{"function":{"name":"x"}}]}}]}""")]
    [InlineData("""{"choices":[{"message":4,"delta":"wrong"}]}""")]
    [InlineData("""{"choices":[{"message":{"tool_calls":[{"type":"function","function":[]}]}}]}""")]
    [InlineData("""{"output":{},"content":[false],"item":[],"response":null}""")]
    [InlineData("""{"item":{"type":"function_call","name":"\uD800"}}""")]
    [InlineData("[1,2]")]
    public void InvalidEventShapesDoNotDiscardLaterValidTools(string malformed)
    {
        var result = ToolParser.Parse(Sse(malformed, GoodEvent));
        Assert.Equal("good", Assert.Single(result.Tools).Id);
        Assert.Contains("invalid structure", result.Note);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("\"0\"")]
    [InlineData("[]")]
    [InlineData("1e99")]
    public void BadDeltaIndexDoesNotLoseAnOpenTool(string index)
    {
        var result = ToolParser.Parse(Sse(
            """{"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"one","name":"read","input":{}}}""",
            "{\"type\":\"content_block_delta\",\"index\":" + index + ",\"delta\":{\"partial_json\":\"bad\"}}",
            """{"type":"content_block_delta","index":0,"delta":{"partial_json":"{\"path\":\"ok\"}"}}"""));
        Assert.Equal("{\"path\":\"ok\"}", Assert.Single(result.Tools).Arguments);
        Assert.Contains("invalid structure", result.Note);
    }

    [Fact]
    public void ParsesCompletedAndStreamedProviderFormatsWithoutTruncatingSmallCalls()
    {
        var result = ToolParser.Parse(Sse(
            GoodEvent,
            """{"content":[{"type":"tool_use","id":"anthropic","name":"search","input":{"query":"hi"}}]}""",
            """{"choices":[{"message":{"tool_calls":[{"type":"function","id":"chat","function":{"name":"list","arguments":"{}"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"stream","function":{"name":"get_","arguments":"{\"x\":"}}]}}]}""",
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"name":"file","arguments":"1}"}}]}}]}""",
            "[DONE]"));
        Assert.Equal(4, result.Tools.Count);
        Assert.Equal("{\"query\":\"hi\"}", result.Tools.Single(t => t.Id == "anthropic").Arguments);
        Assert.Equal("{}", result.Tools.Single(t => t.Id == "chat").Arguments);
        var streamed = result.Tools.Single(t => t.Id == "stream");
        Assert.Equal("get_file", streamed.Name);
        Assert.Equal("{\"x\":1}", streamed.Arguments);
        Assert.Null(result.Note);
    }

    [Fact]
    public void CompletedCallsBoundArgumentsAndIdsIndependentlyOfPayloadSize()
    {
        var body = JsonSerializer.Serialize(new { output = Enumerable.Range(0, 100).Select(i => new
        {
            type = "function_call", call_id = i + new string('i', 1000), name = new string('n', 1000), arguments = new string('a', 12000)
        }) });
        var result = ToolParser.Parse(body);
        Assert.Equal(ToolParser.MaxTools, result.Tools.Count);
        Assert.All(result.Tools, tool =>
        {
            Assert.InRange(tool.Id.Length, 1, ToolParser.MaxLabelCharacters);
            Assert.InRange(tool.Name.Length, 1, ToolParser.MaxLabelCharacters);
            Assert.InRange(tool.Arguments.Length, 0, ToolParser.MaxArgumentCharacters);
        });
        Assert.InRange(result.Tools.Sum(t => t.Arguments.Length), 0, ToolParser.MaxTotalArgumentCharacters);
        Assert.Contains("truncated", result.Note);
    }

    [Fact]
    public void StreamingCallsCannotGrowPastToolOrArgumentLimits()
    {
        var events = Enumerable.Range(0, 100).SelectMany(i => new[]
        {
            JsonSerializer.Serialize(new { type = "content_block_start", index = i,
                content_block = new { type = "tool_use", id = "tool-" + i, name = "read", input = new { } } }),
            JsonSerializer.Serialize(new { type = "content_block_delta", index = i, delta = new { partial_json = new string('x', 6000) } }),
            JsonSerializer.Serialize(new { type = "content_block_delta", index = i, delta = new { partial_json = new string('x', 6000) } })
        }).ToArray();
        var result = ToolParser.Parse(Sse(events));
        Assert.Equal(ToolParser.MaxTools, result.Tools.Count);
        Assert.All(result.Tools, tool => Assert.InRange(tool.Arguments.Length, 0, ToolParser.MaxArgumentCharacters));
        Assert.InRange(result.Tools.Sum(t => t.Arguments.Length), 0, ToolParser.MaxTotalArgumentCharacters);
        Assert.Contains("truncated", result.Note);
    }

    [Fact]
    public void BodyLimitPreservesEarlierSseEventsAndReportsDisplayTruncation()
    {
        var body = Sse(GoodEvent) + "data: {\"text\":\"" + new string('x', ToolParser.MaxResponseCharacters) + "\"}\n\n";
        var result = ToolParser.Parse(body);
        Assert.Equal("good", Assert.Single(result.Tools).Id);
        Assert.Contains("truncated", result.Note);
        Assert.Contains("incomplete JSON", result.Note);
    }
}
