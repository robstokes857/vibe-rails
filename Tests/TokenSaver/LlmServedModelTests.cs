using System.Text;
using TokenSaver;
using Xunit;

namespace Tests.TokenSaver;

public sealed class LlmServedModelTests
{
    private static string? Read(string body, string? mediaType = "application/json", int statusCode = 200) =>
        LlmServedModel.Read(Encoding.UTF8.GetBytes(body), mediaType, statusCode);

    [Theory]
    [InlineData("application/json")]
    [InlineData("Application/Json")]
    [InlineData("application/vnd.provider+json")]
    public void Json_ReadsOnlyTheTopLevelModel(string mediaType)
    {
        Assert.Equal("gpt-snapshot", Read("""
            {"output":[{"model":"nested"}],"response":{"model":"nested-response"},
             "message":{"model":"nested-message"},"model":"gpt-snapshot","error":null}
            """, mediaType));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""["model","snapshot"]""")]
    [InlineData("""{"message":{"model":"nested"}}""")]
    [InlineData("""{"model":null}""")]
    [InlineData("""{"model":3}""")]
    [InlineData("""{"model":{"id":"snapshot"}}""")]
    [InlineData("""{"model":"snapshot","output":[""")]
    [InlineData("""{"model":"snapshot"} trailing""")]
    [InlineData("""{"model":"snapshot",}""")]
    [InlineData("""{"model":"snapshot","error":{"message":"bad request"}}""")]
    [InlineData("""{"type":"error","model":"snapshot"}""")]
    public void Json_MalformedMissingAndErrorBodies_RecordNull(string body)
    {
        Assert.Null(Read(body));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("text/plain")]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    public void UnsupportedContentType_DoesNotGuessFromTheBody(string? mediaType)
    {
        Assert.Null(Read("""{"model":"snapshot"}""", mediaType));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(302)]
    [InlineData(400)]
    [InlineData(500)]
    public void UnsuccessfulHttpStatus_RecordsNullEvenWithAModel(int statusCode)
    {
        Assert.Null(Read("""{"model":"snapshot"}""", statusCode: statusCode));
        Assert.Null(Read("event: message_start\ndata: {\"message\":{\"model\":\"snapshot\"}}\n\n",
            "text/event-stream", statusCode));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void AnthropicSse_ReadsMessageStart_WithSseFraming(string newline)
    {
        var body = string.Join(newline,
            "\uFEFF: keepalive", "id: 1", "retry: 1000", "event: message_start",
            "data: {\"type\":\"message_start\",\"model\":\"root-lookalike\",",
            "data: \"message\":{\"model\":\"claude-snapshot\",\"content\":[],\"usage\":{\"model\":\"nested\"}}}",
            "", "");

        Assert.Equal("claude-snapshot", Read(body, "text/event-stream"));
    }

    [Theory]
    [InlineData("response.created")]
    [InlineData("response.completed")]
    public void ResponsesSse_ReadsResponseModel_FromEventNameOrPayloadType(string eventType)
    {
        Assert.Equal("codex-served", Read(
            $"event: {eventType}\ndata: {{\"response\":{{\"model\":\"codex-served\",\"error\":null}}}}\n\n",
            "text/event-stream"));
        Assert.Equal("codex-served", Read(
            $"data:{{\"type\":\"{eventType}\",\"response\":{{\"model\":\"codex-served\"}}}}\n\n",
            "TEXT/EVENT-STREAM"));
    }

    [Fact]
    public void ResponsesSse_UsesCompletedWhenCreatedHasNoModel()
    {
        Assert.Equal("gpt-snapshot", Read(
            "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{}}\n\n" +
            "event: response.completed\ndata: {\"response\":{\"model\":\"gpt-snapshot\"}}\n\n",
            "text/event-stream"));
    }

    [Fact]
    public void Sse_CompleteFirstEventSurvivesATruncatedLaterEvent()
    {
        Assert.Equal("claude-snapshot", Read(
            "event: message_start\ndata: {\"message\":{\"model\":\"claude-snapshot\"}}\n\n" +
            "event: content_block_delta\ndata: {\"delta\":{\"text\":\"cut off",
            "text/event-stream"));
    }

    [Fact]
    public void ChatCompletionsSse_ReadsTheChunkModel()
    {
        Assert.Equal("grok-snapshot", Read(
            "data: {\"id\":\"c1\",\"object\":\"chat.completion.chunk\",\"model\":\"grok-snapshot\",\"choices\":[]}\n\n" +
            "data: [DONE]\n\n", "text/event-stream"));
    }

    [Theory]
    [InlineData("data: [DONE]\n\n")]
    [InlineData("data: {\"model\":\"unrecognized\"}\n\n")]
    [InlineData("event: content_block_delta\ndata: {\"message\":{\"model\":\"nested\"}}\n\n")]
    [InlineData("event: response.created\ndata: {\"response\":{\"metadata\":{\"model\":\"nested\"}}}\n\n")]
    [InlineData("event: response.completed\ndata: {\"response\":{\"model\":\"snapshot\",\"error\":{\"message\":\"failed\"}}}\n\n")]
    [InlineData("event: error\ndata: {\"type\":\"error\",\"model\":\"snapshot\",\"error\":{\"message\":\"failed\"}}\n\n")]
    [InlineData("event: message_start\ndata: {\"message\":{\"model\":false}}\n\n")]
    [InlineData("event: message_start\ndata: {\"message\":{\"model\":\"snapshot\"}},\n\n")]
    [InlineData("event: message_start\ndata: {\"message\":{\"model\":\"snapshot\"}}\n")]
    public void Sse_MissingMalformedErrorAndIncompleteEvents_RecordNull(string body)
    {
        Assert.Null(Read(body, "text/event-stream"));
    }

    [Fact]
    public void Sse_AnUnreadableEventDoesNotHideALaterModelEvent()
    {
        Assert.Equal("gpt-snapshot", Read(
            "event: response.created\ndata: invalid json\n\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"model\":\"gpt-snapshot\"}}\n\n",
            "text/event-stream"));
    }

    [Fact]
    public void InvalidUtf8Model_RecordsNullAndNeverThrows()
    {
        byte[] json = [.. "{\"model\":\""u8, 0xFF, 0xFE, .. "\"}"u8];
        byte[] sse = [.. "event: message_start\ndata: {\"message\":{\"model\":\""u8,
            0xFF, 0xFE, .. "\"}}\n\n"u8];

        Assert.Null(LlmServedModel.Read(json, "application/json", 200));
        Assert.Null(LlmServedModel.Read(sse, "text/event-stream", 200));
    }

    [Fact]
    public void EscapedModel_IsDecodedWithoutNormalization()
    {
        Assert.Equal("served[1m]", Read("{\"model\":\"served\\u005b1m\\u005d\"}"));
    }
}
