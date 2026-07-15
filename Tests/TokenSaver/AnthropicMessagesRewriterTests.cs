using System.Buffers;
using System.Text;
using System.Text.Json;
using TokenSaver.Minify;
using Xunit;

namespace Tests.TokenSaver;

/// <summary>
/// Pins the tool_result rewriter's contract (token_saving_plan.md §2): ONLY allowlisted shell
/// tool_result strings are touched, every other byte of the request passes through verbatim, and
/// every failure path forwards the original. Byte-identity assertions are deliberate — the
/// transform must be deterministic and idempotent across turns or it thrashes Anthropic prompt
/// caching (§6).
/// </summary>
public class AnthropicMessagesRewriterTests
{
    // "[1mhi[0m  \r\ndone" → minified "hi\ndone" (SGR stripped, trailing ws, CRLF→LF).
    private const string DirtyToolOutput = "\\u001b[1mhi\\u001b[0m  \\r\\ndone";
    private const string MinifiedToolOutput = "hi\\ndone";

    [Fact]
    public void Rewrite_BashToolResultStringContent_IsMinified()
    {
        var body = MessagesBody(
            toolUseName: "Bash",
            toolResultContent: $"\"{DirtyToolOutput}\"");

        var (result, output) = Rewrite(body);

        Assert.True(result.Rewritten);
        Assert.Equal(1, result.ToolResultsMinified);
        Assert.Equal(1, result.ToolResultsSeen);
        Assert.True(result.BytesAfter < result.BytesBefore);
        // Everything except the rewritten token is byte-identical.
        Assert.Equal(body.Replace(DirtyToolOutput, MinifiedToolOutput), output);
        AssertValidJson(output);
    }

    [Fact]
    public void Rewrite_BashToolResultBlockArrayContent_MinifiesOnlyTextBlocks()
    {
        var body = MessagesBody(
            toolUseName: "Bash",
            toolResultContent:
                $$$"""[{"type":"text","text":"{{{DirtyToolOutput}}}"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}}]""");

        var (result, output) = Rewrite(body);

        Assert.True(result.Rewritten);
        Assert.Equal(body.Replace(DirtyToolOutput, MinifiedToolOutput), output);
        Assert.Contains("\"data\":\"AAAA\"", output, StringComparison.Ordinal); // image block untouched
    }

    [Theory]
    [InlineData("Read")]      // never touch file reads — trailing whitespace can be significant
    [InlineData("BashOutput")] // outside the DEFAULT allowlist (the Safe+ tier presets add it)
    public void Rewrite_NonAllowlistedTool_PassesThrough(string toolName)
    {
        var body = MessagesBody(toolName, $"\"{DirtyToolOutput}\"");

        var (result, _) = Rewrite(body);

        Assert.False(result.Rewritten);
        Assert.Equal(result.BytesBefore, result.BytesAfter);
        Assert.Equal(1, result.ToolResultsSeen);
        Assert.Equal(0, result.ToolResultsMinified);
    }

    [Fact]
    public void Rewrite_UnknownToolUseId_PassesThrough()
    {
        var body = $$$"""
            {"model":"claude","messages":[
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_orphan","content":"{{{DirtyToolOutput}}}"}]}
            ]}
            """;

        var (result, _) = Rewrite(body);

        Assert.False(result.Rewritten);
        Assert.Equal(1, result.ToolResultsSeen);
    }

    [Fact]
    public void Rewrite_TypePropertyAfterContent_StillClassifiesBlock()
    {
        // Property order inside a block is not guaranteed by the wire format.
        var body = $$$"""
            {"messages":[
              {"role":"assistant","content":[{"id":"tu_1","name":"Bash","input":{},"type":"tool_use"}]},
              {"role":"user","content":[{"content":"{{{DirtyToolOutput}}}","tool_use_id":"tu_1","type":"tool_result"}]}
            ]}
            """;

        var (result, output) = Rewrite(body);

        Assert.True(result.Rewritten);
        Assert.Equal(body.Replace(DirtyToolOutput, MinifiedToolOutput), output);
    }

    [Fact]
    public void Rewrite_ToolUseInputWithLookAlikeKeys_IsNeverMisread()
    {
        // tool_use.input can contain "type"/"content"/"text" keys; they must be skipped whole.
        var body = $$$"""
            {"messages":[
              {"role":"assistant","content":[{"type":"tool_use","id":"tu_1","name":"Bash","input":{"type":"tool_result","content":"{{{DirtyToolOutput}}}","text":"{{{DirtyToolOutput}}}"}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_1","content":"clean output"}]}
            ]}
            """;

        var (result, _) = Rewrite(body);

        // The only real tool_result is already clean, and the look-alikes inside input are skipped.
        Assert.False(result.Rewritten);
        Assert.Equal(1, result.ToolResultsSeen);
    }

    [Fact]
    public void Rewrite_SystemAndTextBlocks_AreNeverTouched()
    {
        var body = $$$"""
            {"system":"{{{DirtyToolOutput}}}","messages":[
              {"role":"user","content":[{"type":"text","text":"{{{DirtyToolOutput}}}"}]},
              {"role":"assistant","content":[{"type":"tool_use","id":"tu_1","name":"Bash","input":{}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_1","content":"{{{DirtyToolOutput}}}"}]}
            ]}
            """;

        var (result, output) = Rewrite(body);

        Assert.True(result.Rewritten);
        // Exactly one occurrence (the tool_result) was rewritten; system + text kept theirs.
        Assert.Equal(2, CountOccurrences(output, DirtyToolOutput));
        Assert.Single(SplitOccurrences(output, MinifiedToolOutput));
    }

    [Fact]
    public void Rewrite_MalformedJson_FailsOpen()
    {
        var (result, _) = Rewrite("{\"messages\":[{\"role\":");

        Assert.False(result.Rewritten);
    }

    [Fact]
    public void Rewrite_UnchangedBody_PreservesClientEscapingByteForByte()
    {
        // Content is already minimal but uses non-canonical escaping (é for é). A re-emit
        // would normalize it; the splice must not.
        var body = MessagesBody("Bash", "\"caf\\u00e9 output\"");
        var bytes = Encoding.UTF8.GetBytes(body);

        var writer = new ArrayBufferWriter<byte>();
        var result = AnthropicMessagesRewriter.Rewrite(
            bytes, MinifyFlags.Default, default, AnthropicMessagesRewriter.DefaultToolAllowlist, writer);

        Assert.False(result.Rewritten); // caller forwards the original buffer untouched
        Assert.Equal(1, result.ToolResultsSeen);
    }

    [Fact]
    public void Rewrite_MixedChangedAndUnchanged_PreservesUnchangedTokenBytes()
    {
        var body = $$$"""
            {"messages":[
              {"role":"assistant","content":[{"type":"tool_use","id":"tu_1","name":"Bash","input":{}},{"type":"tool_use","id":"tu_2","name":"Bash","input":{}}]},
              {"role":"user","content":[
                {"type":"tool_result","tool_use_id":"tu_1","content":"caf\u00e9 \u0041lready-minimal"},
                {"type":"tool_result","tool_use_id":"tu_2","content":"{{{DirtyToolOutput}}}"}
              ]}
            ]}
            """;

        var (result, output) = Rewrite(body);

        Assert.True(result.Rewritten);
        Assert.Equal(1, result.ToolResultsMinified);
        Assert.Equal(2, result.ToolResultsSeen);
        // The unchanged token keeps its exact client escaping — é/A stay escaped even
        // though a re-emit through the writer would normalize them to raw é/A.
        Assert.Contains("caf\\u00e9 \\u0041lready-minimal", output, StringComparison.Ordinal);
        Assert.Equal(body.Replace(DirtyToolOutput, MinifiedToolOutput), output);
    }

    [Fact]
    public void Rewrite_LoneSurrogateEscape_FailsOpenForThatStringOnly()
    {
        // Node's well-formed JSON.stringify emits a lone \uD800 escape when fixed-length output
        // truncation splits a surrogate pair. CopyString throws InvalidOperationException (NOT
        // JsonException) on it — that string must pass through verbatim, without throwing, while
        // the rest of the body still minifies (adversarial-review finding, 2026-07-12).
        const string loneSurrogate = "\"out\\uD800put  \"";
        var body = TwoResultBody(loneSurrogate, $"\"{DirtyToolOutput}\"");

        var (result, output) = Rewrite(body);

        Assert.True(result.Rewritten);
        Assert.Equal(1, result.ToolResultsMinified);
        Assert.Equal(2, result.ToolResultsSeen);
        // Untouched, trailing spaces and all — the untranscodable token splices verbatim.
        Assert.Contains("out\\uD800put  ", output, StringComparison.Ordinal);
        Assert.Contains(MinifiedToolOutput, output, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_LoneSurrogateOnly_ReturnsUnrewrittenInsteadOfThrowing()
    {
        var body = MessagesBody("Bash", "\"bad\\uD800 output  \"");

        var (result, _) = Rewrite(body);

        Assert.False(result.Rewritten);
        Assert.Equal(result.BytesBefore, result.BytesAfter);
    }

    [Fact]
    public void Rewrite_EmojiReEscapeWouldGrowToken_KeepsOriginalBytes()
    {
        // Minifying strips the trailing spaces, but the writer escapes each raw 4-byte emoji as a
        // 12-byte \uXXXX\uXXXX pair, so the re-emitted token would GROW. The rewriter must splice
        // the original instead — savings can be zero, never negative
        // (adversarial-review finding, 2026-07-12).
        var body = MessagesBody("Bash", "\"a😀b  \\nraw😀  \"");

        var (result, _) = Rewrite(body);

        Assert.False(result.Rewritten);
        Assert.Equal(0, result.BytesSaved);
    }

    [Fact]
    public void Rewrite_MixedEmojiGrowthAndShrinkableStrings_OnlyForwardsTheShrink()
    {
        var body = TwoResultBody("\"a😀b  \\nraw😀  \"", $"\"{DirtyToolOutput}\"");

        var (result, output) = Rewrite(body);

        Assert.True(result.Rewritten);
        Assert.Equal(1, result.ToolResultsMinified);
        Assert.True(result.BytesAfter < result.BytesBefore);
        // The emoji token kept its original bytes (raw UTF-8, trailing spaces intact).
        Assert.Contains("a😀b  \\nraw😀  ", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_IsDeterministic_AndIdempotentAcrossTurns()
    {
        var body = MessagesBody("Bash", $"\"{DirtyToolOutput}\"");
        var (first, firstOutput) = Rewrite(body);
        var (second, secondOutput) = Rewrite(body);

        Assert.True(first.Rewritten);
        Assert.Equal(firstOutput, secondOutput); // determinism: byte-identical across calls

        // The REQUIRED history property: a previously minified body re-submitted (e.g. any resume
        // path) must come back byte-identical, i.e. not rewritten at all.
        var (again, _) = Rewrite(firstOutput);
        Assert.False(again.Rewritten);
    }

    [Theory]
    [InlineData("PowerShell")] // the Windows Claude Code shell tool
    [InlineData("BashOutput")] // background-shell reads, allowlisted from Safe up
    public void Rewrite_TierAllowlist_CoversWindowsAndBackgroundShells(string toolName)
    {
        var body = MessagesBody(toolName, $"\"{DirtyToolOutput}\"");
        var (flags, condense, allowlist) = TokenSaverPresets.For(TokenSaverLevel.Safe);

        var (result, output) = Rewrite(body, flags, condense, allowlist);

        Assert.True(result.Rewritten);
        Assert.Equal(body.Replace(DirtyToolOutput, MinifiedToolOutput), output);
    }

    [Fact]
    public void Rewrite_CondenseOnlyConfiguration_StillRewrites()
    {
        // All minify flags off, condensing on: the no-op gates must consider BOTH stages, or a
        // condense-only bisection state is silently dead.
        var body = MessagesBody("Bash", "\"spam\\nspam\\nspam\\nspam\\ndone\"");
        var condense = new CondenseOptions(DedupeConsecutiveLines: true, TruncateLongOutput: false);

        var (result, output) = Rewrite(body, flags: default(MinifyFlags), condense: condense);

        Assert.True(result.Rewritten);
        Assert.Equal(1, result.Condensed.DedupRunsCollapsed);
        Assert.True(result.Condensed.Changed);
        Assert.Contains("spam [x4]\\ndone", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Rewrite_MediumTier_CondensedBodyIsStableAcrossTurns()
    {
        // Same history property as the minify-only test, but through the full Medium pipeline:
        // a body containing an already-condensed tool_result must come back byte-identical.
        var body = MessagesBody("Bash", "\"spam\\nspam\\nspam\\nspam\\ndone\"");
        var (flags, condense, allowlist) = TokenSaverPresets.For(TokenSaverLevel.Medium);

        var (first, firstOutput) = Rewrite(body, flags, condense, allowlist);
        Assert.True(first.Rewritten);
        Assert.Contains("spam [x4]", firstOutput, StringComparison.Ordinal);

        var (again, _) = Rewrite(firstOutput, flags, condense, allowlist);
        Assert.False(again.Rewritten);
    }

    [Fact]
    public void Rewrite_EmptyAllowlist_DoesNothing()
    {
        var body = MessagesBody("Bash", $"\"{DirtyToolOutput}\"");
        var writer = new ArrayBufferWriter<byte>();

        var result = AnthropicMessagesRewriter.Rewrite(
            Encoding.UTF8.GetBytes(body), MinifyFlags.Default, default, [], writer);

        Assert.False(result.Rewritten);
        Assert.Equal(0, writer.WrittenCount);
    }

    private static string TwoResultBody(string content1, string content2) =>
        $$$"""
        {"messages":[
          {"role":"assistant","content":[{"type":"tool_use","id":"tu_1","name":"Bash","input":{}},{"type":"tool_use","id":"tu_2","name":"Bash","input":{}}]},
          {"role":"user","content":[
            {"type":"tool_result","tool_use_id":"tu_1","content":{{{content1}}}},
            {"type":"tool_result","tool_use_id":"tu_2","content":{{{content2}}}}
          ]}
        ]}
        """;

    private static string MessagesBody(string toolUseName, string toolResultContent) =>
        $$$"""
        {"model":"claude-sonnet-5","max_tokens":32000,"messages":[
          {"role":"assistant","content":[{"type":"tool_use","id":"tu_1","name":"{{{toolUseName}}}","input":{"command":"git status"}}]},
          {"role":"user","content":[{"type":"tool_result","tool_use_id":"tu_1","content":{{{toolResultContent}}}}]}
        ],"metadata":{"user_id":"abc"}}
        """;

    private static (ToolOutputRewriteResult Result, string Output) Rewrite(
        string body,
        MinifyFlags? flags = null,
        CondenseOptions condense = default,
        IReadOnlyCollection<string>? allowlist = null)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var writer = new ArrayBufferWriter<byte>();
        var result = AnthropicMessagesRewriter.Rewrite(
            bytes,
            flags ?? MinifyFlags.Default,
            condense,
            allowlist ?? AnthropicMessagesRewriter.DefaultToolAllowlist,
            writer);
        var output = result.Rewritten
            ? Encoding.UTF8.GetString(writer.WrittenSpan)
            : body;
        if (result.Rewritten)
            Assert.Equal(result.BytesAfter, writer.WrittenCount);
        return (result, output);
    }

    private static void AssertValidJson(string json)
    {
        using var document = JsonDocument.Parse(json);
    }

    private static int CountOccurrences(string haystack, string needle) =>
        SplitOccurrences(haystack, needle).Count;

    private static List<int> SplitOccurrences(string haystack, string needle)
    {
        var hits = new List<int>();
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            hits.Add(index);
            index += needle.Length;
        }

        return hits;
    }
}
