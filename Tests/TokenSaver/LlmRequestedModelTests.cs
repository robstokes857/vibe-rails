using System.Text;
using TokenSaver;
using Xunit;

namespace Tests.TokenSaver;

/// <summary>
/// The exchange log records what each request asked for (VIBE-155). The bodies below are trimmed
/// from real traffic on 2026-10-13: the fields sit in different places per API, a lookalike field
/// nested deeper must never be mistaken for the request's own, and a body that cannot be read
/// records nulls instead of throwing on the relay.
/// </summary>
public sealed class LlmRequestedModelTests
{
    private static LlmRequestedModel Read(string body) => LlmRequestedModel.Read(Encoding.UTF8.GetBytes(body));

    [Fact]
    public void CodexResponses_ReadsModelEffortAndServiceTier()
    {
        var requested = Read("""
            {"model":"gpt-6.1-sol","stream":true,"service_tier":"priority",
             "input":[{"type":"message","role":"user","content":[{"type":"input_text","text":"hi"}]}],
             "tool_choice":"auto","parallel_tool_calls":true,
             "reasoning":{"effort":"xhigh","context":"all_turns"},"store":false,
             "include":["reasoning.encrypted_content"],"prompt_cache_key":"k","text":{"verbosity":"low"}}
            """);

        Assert.Equal(new LlmRequestedModel("gpt-6.1-sol", "xhigh", "priority"), requested);
    }

    [Fact]
    public void CodexResponses_WithoutServiceTier_LeavesTierNull()
    {
        // Codex omits service_tier at its default speed; absence is the signal, not a value to invent.
        var requested = Read("""{"model":"gpt-6.1-sol","input":[],"reasoning":{"effort":"low"}}""");

        Assert.Equal(new LlmRequestedModel("gpt-6.1-sol", "low", null), requested);
    }

    [Fact]
    public void AnthropicMessages_ReadsModelAndOutputConfigEffort()
    {
        var requested = Read("""
            {"model":"claude-opus-5-5","messages":[{"role":"user","content":"hi"}],"system":[],
             "tools":[],"metadata":{"user_id":"u"},"max_tokens":64000,
             "thinking":{"type":"adaptive","display":"updates"},
             "output_config":{"format":{"type":"json_schema","schema":{"type":"object"}},"effort":"high"},
             "stream":true}
            """);

        Assert.Equal(new LlmRequestedModel("claude-opus-5-5", "high", null), requested);
    }

    [Fact]
    public void ChatCompletions_ReadsTopLevelReasoningEffort()
    {
        var requested = Read("""{"model":"grok-build","messages":[],"reasoning_effort":"medium","service_tier":"default"}""");

        Assert.Equal(new LlmRequestedModel("grok-build", "medium", "default"), requested);
    }

    [Fact]
    public void EffortSourceWithoutEffort_DoesNotEraseAnother()
    {
        var requested = Read("""{"model":"m","reasoning_effort":"high","output_config":{"format":{"type":"text"}}}""");

        Assert.Equal("high", requested.ReasoningEffort);
    }

    [Fact]
    public void NestedLookalikes_AreNotTheRequestsOwnFields()
    {
        // Tool schemas and conversation items carry their own "model", "effort", "reasoning" and
        // "service_tier" keys. Only the root object's fields describe the request.
        var requested = Read("""
            {"input":[{"type":"reasoning","model":"nested","reasoning":{"effort":"nested"},"service_tier":"nested"}],
             "tools":[{"type":"function","parameters":{"properties":{"model":{"type":"string"},"effort":{"type":"string"}}}}],
             "output_config":{"format":{"effort":"nested"}},
             "model":"gpt-6.1-sol"}
            """);

        Assert.Equal(new LlmRequestedModel("gpt-6.1-sol", null, null), requested);
    }

    [Fact]
    public void NonStringValues_ReadAsNullWithoutLosingTheOthers()
    {
        var requested = Read("""{"model":{"id":"x"},"reasoning":"high","service_tier":7,"output_config":{"effort":5}}""");

        Assert.Equal(default, requested);

        requested = Read("""{"model":null,"reasoning":{"effort":"low"},"service_tier":["priority"]}""");

        Assert.Equal(new LlmRequestedModel(null, "low", null), requested);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""["model","gpt-6.1-sol"]""")]
    [InlineData("\"gpt-6.1-sol\"")]
    // Truncated: the model was read before the body ran out, and must still not be recorded.
    [InlineData("""{"model":"gpt-6.1-sol","input":[{"type":"message",""")]
    [InlineData("""{"model":"gpt-6.1-sol"} trailing""")]
    [InlineData("""{"model":"gpt-6.1-sol",}""")]
    public void UnreadableBody_RecordsNothingAndNeverThrows(string body)
    {
        Assert.Equal(default, Read(body));
    }

    [Fact]
    public void InvalidUtf8_RecordsNothingAndNeverThrows()
    {
        byte[] body = [.. "{\"model\":\""u8, 0xFF, 0xFE, .. "\"}"u8];

        Assert.Equal(default, LlmRequestedModel.Read(body));
    }

    [Fact]
    public void EscapedValues_AreDecoded()
    {
        // Built from pieces so this file holds the JSON escape itself, not the character it encodes.
        const string backslash = "\\";
        var requested = Read("{\"model\":\"claude-opus-5-5" + backslash + "u005b1m" + backslash + "u005d\"}");

        Assert.Equal("claude-opus-5-5[1m]", requested.Model);
    }
}
