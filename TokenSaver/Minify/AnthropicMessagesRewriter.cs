using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TokenSaver.Minify;

/// <summary>
/// Rewrites an Anthropic <c>/v1/messages</c> request body, minifying ONLY the tool_result strings
/// that came from an allowlisted shell tool (token_saving_plan.md §2 "What we touch — and only
/// this"). Strategy: a two-pass <see cref="Utf8JsonReader"/> scan + raw-byte splice — every byte
/// outside the rewritten string tokens is copied verbatim, so the client's own escaping, property
/// order, number formatting, <c>cache_control</c> blobs, and unknown future fields are untouched
/// by construction. No DOM, no reflection (AOT-safe).
///
/// Pass 1 walks <c>messages[].content[]</c> blocks, correlating <c>tool_use.id → name</c> and
/// collecting the byte ranges of qualifying <c>tool_result</c> string tokens (string content, or
/// each <c>{type:"text"}.text</c> inside a block array; property order within a block is not
/// guaranteed, so facts are buffered and decided at block close). Pass 2 splices: unchanged
/// strings are copied token-verbatim (byte-stable no-op); changed strings are re-emitted with
/// minimal escaping. Anything unrecognized — unknown tool, missing id, image blocks, text blocks,
/// system prompt, tool_use inputs — passes through untouched.
///
/// Fail-open: any <see cref="JsonException"/> returns "not rewritten" and the caller forwards the
/// original bytes. Determinism/idempotency ride on <see cref="OutputMinifier"/>'s and
/// <see cref="OutputCondenser"/>'s guarantees (each qualifying string runs
/// <c>condense(minify(text))</c>): the CLI re-serializes its own history each turn (it never sees
/// our rewrite), and this function is pure, so upstream sees byte-identical prefixes every turn —
/// prompt caching stays intact.
/// </summary>
public static class AnthropicMessagesRewriter
{
    /// <summary>
    /// The original v1 allowlist, kept as the fallback when no tier resolves one (see
    /// <see cref="TokenSaverPresets"/> for the per-level lists). A tool rename upstream makes
    /// savings drop to zero (visible in the seen-vs-minified counters), never corrupts anything —
    /// unknown names pass through.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultToolAllowlist = ["Bash"];

    private static readonly JsonWriterOptions StringWriterOptions = new()
    {
        // Minimal escaping (only quotes/backslash/controls, raw UTF-8 stays raw) keeps byte counts
        // honest and matches what JSON.stringify produces. Safe here: the value lands inside a JSON
        // string in an HTTPS body, never in HTML. SkipValidation because we emit lone string values.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        SkipValidation = true
    };

    public static ToolOutputRewriteResult Rewrite(
        ReadOnlySpan<byte> utf8Body,
        MinifyFlags flags,
        CondenseOptions condense,
        IReadOnlyCollection<string> toolAllowlist,
        IBufferWriter<byte> output)
    {
        var stats = new MinifyStats();
        var condenseStats = new CondenseStats();
        var unchanged = new ToolOutputRewriteResult(
            false, utf8Body.Length, utf8Body.Length, 0, 0, stats);
        // Both stages no-op (or nothing allowlisted) means a guaranteed passthrough — checking
        // only the minify flags here would leave a condense-only configuration silently dead.
        if (utf8Body.IsEmpty || (flags.IsNoOp && condense.IsNoOp) || toolAllowlist.Count == 0)
            return unchanged;

        List<ToolResultEntry> toolResults;
        Dictionary<string, string> toolNames;
        try
        {
            (toolNames, toolResults) = LocateToolResults(utf8Body);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            // Fail open: not JSON we understand — including GetString() throwing
            // InvalidOperationException on untranscodable id/name values — forward original bytes.
            return unchanged;
        }

        var seen = toolResults.Count;
        var maxTokenLength = 0;
        var qualifying = new List<ToolResultEntry>();
        foreach (var entry in toolResults)
        {
            if (entry.ToolUseId is null
                || entry.Ranges.Count == 0
                || !toolNames.TryGetValue(entry.ToolUseId, out var name)
                || !IsAllowed(toolAllowlist, name))
            {
                continue;
            }

            qualifying.Add(entry);
            foreach (var (start, end) in entry.Ranges)
                maxTokenLength = Math.Max(maxTokenLength, end - start);
        }

        if (qualifying.Count == 0)
            return unchanged with { ToolResultsSeen = seen };

        // A JSON string token's UTF-16 char count never exceeds its UTF-8 byte count (escapes only
        // shrink), so one rented trio sized to the largest token serves every splice.
        var unescaped = ArrayPool<char>.Shared.Rent(maxTokenLength);
        var minified = ArrayPool<char>.Shared.Rent(maxTokenLength);
        var condensed = ArrayPool<char>.Shared.Rent(maxTokenLength);
        // Changed strings are re-emitted into a scratch buffer first so the result can be size-
        // checked against the original token: the encoder escapes non-BMP chars as \uXXXX\uXXXX
        // pairs (a raw 4-byte emoji becomes 12 bytes), so a "minified" emoji-heavy string can come
        // out LARGER — in that case the original token is spliced instead, keeping the
        // never-grows promise at the wire level.
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
                    // Stats are merged only for accepted rewrites so the diagnostic counters can't
                    // drift when a candidate is discarded.
                    var candidateStats = stats;
                    var candidateCondense = condenseStats;
                    try
                    {
                        var tokenReader = new Utf8JsonReader(token);
                        tokenReader.Read();
                        var charLength = tokenReader.CopyString(unescaped);

                        // The two-stage pipeline: lossless minify, then (when enabled) the lossy
                        // condense pass over whatever survived. Either stage alone marks the
                        // string changed.
                        var current = (ReadOnlySpan<char>)unescaped.AsSpan(0, charLength);
                        var changed = false;
                        if (OutputMinifier.TryMinify(
                                current, flags, minified, out var written, ref candidateStats))
                        {
                            current = minified.AsSpan(0, written);
                            changed = true;
                        }

                        if (!condense.IsNoOp && OutputCondenser.TryCondense(
                                current, condense, condensed, out var condensedWritten,
                                ref candidateCondense))
                        {
                            current = condensed.AsSpan(0, condensedWritten);
                            changed = true;
                        }

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
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
                    {
                        // Per-string fail-open: CopyString throws InvalidOperationException (not
                        // JsonException) on untranscodable content — e.g. a lone surrogate escape,
                        // which well-formed JSON.stringify genuinely emits when output truncation
                        // splits a surrogate pair. That one string passes through; the rest of the
                        // body still minifies.
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
            // Outer fail-open (mirrors token_saving_plan.md §6): anything unparseable at the body
            // level forwards the original bytes. Callers must ignore `output`'s partial contents
            // when Rewritten is false.
            return unchanged with { ToolResultsSeen = seen };
        }
        finally
        {
            ArrayPool<char>.Shared.Return(unescaped);
            ArrayPool<char>.Shared.Return(minified);
            ArrayPool<char>.Shared.Return(condensed);
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
        public readonly List<(int Start, int End)> Ranges = [];
    }

    /// <summary>
    /// Pass 1: scan <c>messages[].content[]</c>, returning the tool_use id→name map and every
    /// tool_result block with the byte ranges of its candidate string tokens (quotes included).
    /// Throws <see cref="JsonException"/> on malformed input — the caller fails open.
    /// </summary>
    private static (Dictionary<string, string> ToolNames, List<ToolResultEntry> ToolResults)
        LocateToolResults(ReadOnlySpan<byte> utf8Body)
    {
        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        var toolResults = new List<ToolResultEntry>();

        var reader = new Utf8JsonReader(utf8Body);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return (toolNames, toolResults);

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.CurrentDepth == 1 && reader.ValueTextEquals("messages"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartArray)
                    ScanMessages(ref reader, toolNames, toolResults);
                else
                    reader.Skip();
            }
            else
            {
                reader.Skip();
            }
        }

        return (toolNames, toolResults);
    }

    private static void ScanMessages(
        ref Utf8JsonReader reader,
        Dictionary<string, string> toolNames,
        List<ToolResultEntry> toolResults)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
                ScanMessage(ref reader, toolNames, toolResults);
            else
                reader.Skip();
        }
    }

    private static void ScanMessage(
        ref Utf8JsonReader reader,
        Dictionary<string, string> toolNames,
        List<ToolResultEntry> toolResults)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.ValueTextEquals("content"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartArray)
                {
                    // Block-array form. (Plain-string message content is a text message — never touched.)
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType == JsonTokenType.StartObject)
                            ScanContentBlock(ref reader, toolNames, toolResults);
                        else
                            reader.Skip();
                    }
                }
                else if (reader.TokenType is JsonTokenType.StartObject)
                {
                    reader.Skip();
                }
            }
            else
            {
                reader.Skip();
            }
        }
    }

    /// <summary>
    /// Scans one <c>content[]</c> block. Property order is not guaranteed ("type" may follow
    /// "content"), so facts are buffered and the block is classified only at its EndObject.
    /// Unknown properties — including tool_use "input", whose nested JSON may contain look-alike
    /// "type"/"content" keys — are skipped whole, so they can never be misread as block facts.
    /// </summary>
    private static void ScanContentBlock(
        ref Utf8JsonReader reader,
        Dictionary<string, string> toolNames,
        List<ToolResultEntry> toolResults)
    {
        var isToolUse = false;
        var isToolResult = false;
        string? id = null;
        string? name = null;
        var entry = new ToolResultEntry();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.ValueTextEquals("type"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                {
                    isToolUse = reader.ValueTextEquals("tool_use"u8);
                    isToolResult = reader.ValueTextEquals("tool_result"u8);
                }
                else
                {
                    reader.Skip();
                }
            }
            else if (reader.ValueTextEquals("id"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                    id = reader.GetString();
                else
                    reader.Skip();
            }
            else if (reader.ValueTextEquals("name"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                    name = reader.GetString();
                else
                    reader.Skip();
            }
            else if (reader.ValueTextEquals("tool_use_id"u8))
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
                    entry.Ranges.Add(CurrentStringTokenRange(ref reader));
                else if (reader.TokenType == JsonTokenType.StartArray)
                    ScanToolResultContent(ref reader, entry);
                else if (reader.TokenType == JsonTokenType.StartObject)
                    reader.Skip();
            }
            else
            {
                reader.Skip();
            }
        }

        if (isToolUse && id is not null && name is not null)
            toolNames[id] = name; // indexer, not Add: a duplicated id must not throw the rewrite away
        else if (isToolResult)
            toolResults.Add(entry);
    }

    private static void ScanToolResultContent(ref Utf8JsonReader reader, ToolResultEntry entry)
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
                    reader.Skip(); // image "source", "cache_control", anything else
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
