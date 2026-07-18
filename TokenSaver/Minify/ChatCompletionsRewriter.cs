using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TokenSaver.Pipeline;
using TokenSaver.Shape;

namespace TokenSaver.Minify;

/// <summary>
/// Rewrites an OpenAI Chat Completions (<c>/v1/chat/completions</c>) request body, minifying ONLY
/// the tool output strings that came from an allowlisted OpenCode shell tool — never the system
/// prompt, user messages, or the model's replies. This is the zai/Z.AI (GLM) provider's wire
/// format as routed through OpenCode, and is a third shape distinct from both the Anthropic
/// Messages API (<see cref="AnthropicMessagesRewriter"/>) and the OpenAI Responses API
/// (<see cref="CodexResponsesRewriter"/>).
///
/// Chat Completions represents the conversation as a flat <c>messages[]</c> array. An assistant
/// turn that invoked a tool carries <c>tool_calls[]</c> (each with <c>id</c> + <c>function.name</c>
/// + <c>function.arguments</c>); the corresponding output is a SEPARATE message with
/// <c>role:"tool"</c>, <c>tool_call_id</c> matching the call, and <c>content</c> holding the
/// output string (or an array of <c>{type:"text",text:...}</c> parts). This rewriter correlates
/// <c>tool_call_id → name</c> and raw-byte-splices only the qualifying tool-message content.
///
/// Strategy mirrors the other rewriters: a two-pass <see cref="Utf8JsonReader"/> scan + raw-byte
/// splice — every byte outside the rewritten string tokens is copied verbatim, so the client's own
/// escaping, property order, and unknown future fields are untouched by construction. No DOM, no
/// reflection (AOT-safe). Fail-open: any <see cref="JsonException"/> returns "not rewritten" and
/// the caller forwards the original bytes.
/// </summary>
public static class ChatCompletionsRewriter
{
    /// <summary>
    /// The fallback allowlist for callers that pass none. The real allowlist comes from the enabled
    /// scope ids — see <see cref="CompressionCatalog.Scopes"/>. A tool rename upstream makes
    /// savings drop to zero (visible in the seen-vs-minified counters), never corrupts anything.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultToolAllowlist = ["bash"];

    private static readonly JsonWriterOptions StringWriterOptions = new()
    {
        // Minimal escaping (only quotes/backslash/controls, raw UTF-8 stays raw) keeps byte counts
        // honest and matches what JSON.stringify produces. Safe here: the value lands inside a JSON
        // string in an HTTPS body, never in HTML. SkipValidation because we emit lone string values.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true
    };

    /// <summary>Compatibility adapter for callers that predate <see cref="CompressionPlan"/>.</summary>
    public static ToolOutputRewriteResult Rewrite(
        ReadOnlySpan<byte> utf8Body,
        MinifyFlags flags,
        CondenseOptions condense,
        IReadOnlyCollection<string> toolAllowlist,
        IBufferWriter<byte> output) =>
        Rewrite(
            utf8Body,
            CompressionPlan.FromLegacy(flags, condense, zaiAllowlist: [.. toolAllowlist]),
            output);

    /// <summary>
    /// Rewrites with one immutable request plan. Keeping flags, shapes, scopes, and diagnostic ids
    /// in this single value makes contradictory runtime configuration unrepresentable.
    /// </summary>
    /// <param name="captures">
    /// Optional before/after recorder. Null skips materializing raw strings entirely, which is why
    /// the check is inside the loop rather than at the call site.
    /// </param>
    public static ToolOutputRewriteResult Rewrite(
        ReadOnlySpan<byte> utf8Body,
        CompressionPlan plan,
        IBufferWriter<byte> output,
        ICompressionCaptureSink? captures = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var stats = new MinifyStats();
        var condenseStats = new CondenseStats();
        var unchanged = new ToolOutputRewriteResult(
            false, utf8Body.Length, utf8Body.Length, 0, 0, stats);
        var toolAllowlist = plan.ZaiAllowlist;

        // Every stage no-op (or nothing allowlisted) means a guaranteed passthrough — checking only
        // the minify flags here would leave a condense-only or shape-only config silently dead.
        if (utf8Body.IsEmpty || plan.IsNoOp || toolAllowlist.Count == 0)
            return unchanged;

        List<ToolResultEntry> toolResults;
        Dictionary<string, ToolUseInfo> toolUses;
        try
        {
            (toolUses, toolResults) = LocateToolResults(utf8Body);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Fail open: not JSON we understand — forward original bytes.
            return unchanged;
        }

        var seen = toolResults.Count;
        var maxTokenLength = 0;
        var qualifying = new List<ToolResultEntry>();
        foreach (var entry in toolResults)
        {
            if (entry.ToolUseId is null
                || entry.Ranges.Count == 0
                || !toolUses.TryGetValue(entry.ToolUseId, out var use)
                || !IsAllowed(toolAllowlist, use.Name))
            {
                continue;
            }

            entry.ToolName = use.Name;
            entry.Command = use.Command;
            qualifying.Add(entry);
            foreach (var (start, end) in entry.Ranges)
                maxTokenLength = Math.Max(maxTokenLength, end - start);
        }

        if (qualifying.Count == 0)
            return unchanged with { ToolResultsSeen = seen };

        // A JSON string token's UTF-16 char count never exceeds its UTF-8 byte count (escapes only
        // shrink), so one rented trio sized to the largest token serves every splice.
        var unescaped = ArrayPool<char>.Shared.Rent(maxTokenLength);
        using var buffers = new PipelineScratch(maxTokenLength);
        using var scratch = new PooledBufferWriter(maxTokenLength);
        using var writer = new Utf8JsonWriter(scratch, StringWriterOptions);
        try
        {
            var cursor = 0;
            var bytesWritten = 0;
            var minifiedResults = 0;
            var anyChanged = false;

            foreach (var entry in qualifying)
            {
                var entryChanged = false;
                foreach (var (start, end) in entry.Ranges)
                {
                    var verbatim = utf8Body[cursor..start];
                    output.Write(verbatim);
                    bytesWritten += verbatim.Length;

                    var token = utf8Body[start..end];
                    var accepted = false;
                    var candidateStats = stats;
                    var candidateCondense = condenseStats;
                    try
                    {
                        var tokenReader = new Utf8JsonReader(token);
                        tokenReader.Read();
                        var charLength = tokenReader.CopyString(unescaped);

                        var raw = (ReadOnlySpan<char>)unescaped.AsSpan(0, charLength);

                        var trace = captures is not null ? new List<StageTrace>() : null;
                        var current = CompressionPipeline.Run(
                            raw,
                            plan,
                            CommandShapes.Classify(entry.Command),
                            buffers,
                            out var changed,
                            ref candidateStats,
                            ref candidateCondense,
                            trace);

                        if (changed)
                        {
                            scratch.Clear();
                            writer.Reset(scratch);
                            writer.WriteStringValue(current);
                            writer.Flush();
                            if (scratch.WrittenCount < token.Length)
                            {
                                output.Write(scratch.WrittenMemory.Span);
                                bytesWritten += scratch.WrittenCount;
                                stats = candidateStats;
                                condenseStats = candidateCondense;
                                accepted = true;
                                entryChanged = true;
                                anyChanged = true;
                            }
                        }

                        if (trace is not null)
                        {
                            captures!.Capture(new CompressionCapture(
                                Guid.NewGuid(),
                                "zai",
                                entry.ToolName,
                                entry.Command,
                                raw.ToString(),
                                current.ToString(),
                                trace,
                                [.. plan.EnabledIds],
                                RewriteAccepted: accepted));
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
                    {
                        // Per-string fail-open: that one string passes through; the rest still minifies.
                        accepted = false;
                    }

                    if (!accepted)
                    {
                        // Unchanged, over-sized re-emit, or fail-open: splice the ORIGINAL token
                        // bytes so the client's exact escaping survives — byte-stable no-op.
                        output.Write(token);
                        bytesWritten += token.Length;
                    }

                    cursor = end;
                }

                if (entryChanged)
                    minifiedResults++;
            }

            if (!anyChanged)
                return unchanged with { ToolResultsSeen = seen, Transforms = stats };

            var tail = utf8Body[cursor..];
            output.Write(tail);
            bytesWritten += tail.Length;

            return new ToolOutputRewriteResult(
                true, utf8Body.Length, bytesWritten, minifiedResults, seen, stats, condenseStats);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Outer fail-open: anything unparseable at the body level forwards the original bytes.
            return unchanged with { ToolResultsSeen = seen };
        }
        finally
        {
            ArrayPool<char>.Shared.Return(unescaped);
        }
    }

    private static bool IsAllowed(IReadOnlyCollection<string> allowlist, string name)
    {
        foreach (var allowed in allowlist)
        {
            if (string.Equals(allowed, name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private sealed class ToolResultEntry
    {
        public string? ToolUseId;

        /// <summary>Resolved from the matching assistant tool_call during the qualifying pass, not
        /// during the scan: a tool message can legally appear before the call it answers.</summary>
        public string ToolName = string.Empty;
        public string? Command;
        public readonly List<(int Start, int End)> Ranges = [];
    }

    /// <summary>What a tool_call tells us about the output that will come back: the tool's name
    /// (which decides whether we may touch it at all) and, for shell tools, the command (which
    /// decides which shape filter, if any, understands it).</summary>
    private readonly record struct ToolUseInfo(string Name, string? Command);

    /// <summary>
    /// Pass 1: scan <c>messages[]</c>, returning the tool_call id→name map and every tool message
    /// with the byte ranges of its candidate content string tokens (quotes included). Throws
    /// <see cref="JsonException"/> on malformed input — the caller fails open.
    /// </summary>
    private static (Dictionary<string, ToolUseInfo> ToolUses, List<ToolResultEntry> ToolResults)
        LocateToolResults(ReadOnlySpan<byte> utf8Body)
    {
        var toolUses = new Dictionary<string, ToolUseInfo>(StringComparer.Ordinal);
        var toolResults = new List<ToolResultEntry>();

        var reader = new Utf8JsonReader(utf8Body);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return (toolUses, toolResults);

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.CurrentDepth == 1 && reader.ValueTextEquals("messages"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartArray)
                    ScanMessages(ref reader, toolUses, toolResults);
                else
                    reader.Skip();
            }
            else
            {
                reader.Skip();
            }
        }

        return (toolUses, toolResults);
    }

    private static void ScanMessages(
        ref Utf8JsonReader reader,
        Dictionary<string, ToolUseInfo> toolUses,
        List<ToolResultEntry> toolResults)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
                ScanMessage(ref reader, toolUses, toolResults);
            else
                reader.Skip();
        }
    }

    /// <summary>
    /// Scans one <c>messages[]</c> entry. An assistant message's <c>tool_calls[]</c> populate the
    /// id→(name,command) map; a <c>role:"tool"</c> message's <c>content</c> populates the result
    /// ranges. Property order is not guaranteed, so a tool message buffers its tool_call_id and
    /// content ranges and is recorded at EndObject.
    /// </summary>
    private static void ScanMessage(
        ref Utf8JsonReader reader,
        Dictionary<string, ToolUseInfo> toolUses,
        List<ToolResultEntry> toolResults)
    {
        var isToolMessage = false;
        var entry = new ToolResultEntry();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.ValueTextEquals("role"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                    isToolMessage = reader.ValueTextEquals("tool"u8);
                else
                    reader.Skip();
            }
            else if (reader.ValueTextEquals("tool_call_id"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                    entry.ToolUseId = reader.GetString();
                else
                    reader.Skip();
            }
            else if (reader.ValueTextEquals("content"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                {
                    entry.Ranges.Add(CurrentStringTokenRange(ref reader));
                }
                else if (reader.TokenType == JsonTokenType.StartArray)
                {
                    // Array-form content (e.g. [{type:"text",text:...}]). Only text parts are
                    // candidates; everything else (images, etc.) is skipped whole.
                    ScanToolContentArray(ref reader, entry);
                }
                else if (reader.TokenType == JsonTokenType.StartObject)
                {
                    reader.Skip();
                }
            }
            else if (reader.ValueTextEquals("tool_calls"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartArray)
                    ScanToolCalls(ref reader, toolUses);
                else
                    reader.Skip();
            }
            else
            {
                // Unknown property on the message. Skip whole so nested look-alike keys can never
                // be misread as message facts.
                reader.Skip();
            }
        }

        if (isToolMessage)
            toolResults.Add(entry);
    }

    /// <summary>
    /// Scans an assistant message's <c>tool_calls[]</c>. Each entry is <c>{id, type, function:
    /// {name, arguments}}</c>; <c>arguments</c> is a JSON-encoded STRING whose parsed object may
    /// carry <c>command</c> (string or array) for shell tools. Records id→(name,command).
    /// </summary>
    private static void ScanToolCalls(
        ref Utf8JsonReader reader,
        Dictionary<string, ToolUseInfo> toolUses)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            string? id = null;
            string? name = null;
            string? arguments = null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                if (reader.ValueTextEquals("id"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                        id = reader.GetString();
                    else
                        reader.Skip();
                }
                else if (reader.ValueTextEquals("function"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.StartObject)
                        (name, arguments) = ScanFunction(ref reader);
                    else
                        reader.Skip();
                }
                else
                {
                    reader.Skip();
                }
            }

            if (id is not null && name is not null)
            {
                // indexer, not Add: a duplicated id must not throw the rewrite away
                toolUses[id] = new ToolUseInfo(name, ExtractCommand(arguments));
            }
        }
    }

    /// <summary>Reads <c>{name, arguments}</c> from a tool_call's <c>function</c> object.</summary>
    private static (string? Name, string? Arguments) ScanFunction(ref Utf8JsonReader reader)
    {
        string? name = null;
        string? arguments = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.ValueTextEquals("name"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                    name = reader.GetString();
                else
                    reader.Skip();
            }
            else if (reader.ValueTextEquals("arguments"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                    arguments = reader.GetString();
                else
                    reader.Skip();
            }
            else
            {
                reader.Skip();
            }
        }

        return (name, arguments);
    }

    /// <summary>
    /// Best-effort extraction of <c>command</c> from a tool_call's <c>arguments</c> JSON string.
    /// The command drives shape filters (git-status / grep / find grouping) — never the rewrite
    /// gate — so any parse failure safely degrades to "no shape filter" (null). Accepts both
    /// string and array-of-string command forms; arrays are joined with spaces.
    /// </summary>
    private static string? ExtractCommand(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return null;

        try
        {
            // Re-encode the decoded string to UTF-8 for a Utf8JsonReader pass. arguments payloads
            // are small tool inputs, so the transient allocation is acceptable and keeps this
            // AOT-safe (no DOM/reflection).
            var bytes = Encoding.UTF8.GetBytes(arguments);
            var reader = new Utf8JsonReader(bytes);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                if (reader.ValueTextEquals("command"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                        return reader.GetString();
                    if (reader.TokenType == JsonTokenType.StartArray)
                        return ReadCommandArray(ref reader);
                    reader.Skip();
                    return null;
                }

                reader.Skip();
            }

            return null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static string ReadCommandArray(ref Utf8JsonReader reader)
    {
        var parts = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
                parts.Add(reader.GetString() ?? string.Empty);
            else
                reader.Skip();
        }

        return string.Join(' ', parts);
    }

    /// <summary>Scans an array-form tool message <c>content</c> for <c>{type:"text",text:...}</c>
    /// parts and records their string-token byte ranges.</summary>
    private static void ScanToolContentArray(ref Utf8JsonReader reader, ToolResultEntry entry)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            var isText = false;
            (int Start, int End)? textRange = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                        isText = reader.ValueTextEquals("text"u8);
                    else
                        reader.Skip();
                }
                else if (reader.ValueTextEquals("text"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                        textRange = CurrentStringTokenRange(ref reader);
                    else
                        reader.Skip();
                }
                else
                {
                    reader.Skip(); // image_url, cache_control, anything else
                }
            }

            if (isText && textRange is { } range)
                entry.Ranges.Add(range);
        }
    }

    /// <summary>Byte range of the string token the reader is currently on, quotes included.</summary>
    private static (int Start, int End) CurrentStringTokenRange(ref Utf8JsonReader reader) =>
        ((int)reader.TokenStartIndex, (int)reader.BytesConsumed);
}
