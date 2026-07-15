using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TokenSaver.Minify;

/// <summary>
/// Rewrites shell-tool outputs in an OpenAI Responses API request body. Codex represents its
/// conversation as <c>input[]</c> items: a tool call carries <c>name</c> + <c>call_id</c>, and the
/// corresponding output carries the same <c>call_id</c>. This rewriter correlates those items and
/// raw-byte-splices only output strings belonging to an allowlisted shell tool.
///
/// Both ordinary <c>function_call(_output)</c> and <c>custom_tool_call(_output)</c> pairs are
/// supported, including array-form output content containing <c>input_text</c>. Provider-native
/// <c>local_shell_call_output</c> and <c>shell_call_output</c> items are intrinsically shell output
/// and do not need name correlation. Every other byte remains exactly as Codex serialized it.
/// </summary>
public static class CodexResponsesRewriter
{
    /// <summary>
    /// Shell function names used by current and earlier Codex clients. Unknown tools fail toward
    /// no savings: their outputs are observed in the seen counter but never rewritten.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultToolAllowlist =
        ["shell_command", "exec_command", "write_stdin"];

    private static readonly JsonWriterOptions StringWriterOptions = new()
    {
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
        if (utf8Body.IsEmpty || (flags.IsNoOp && condense.IsNoOp))
            return unchanged;

        List<ResponseItem> items;
        try
        {
            items = LocateResponseItems(utf8Body);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return unchanged;
        }

        var toolNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.CallId is not null && item.Name is not null && item.IsToolCall)
                toolNames[item.CallId] = item.Name;
        }

        var seen = 0;
        var maxTokenLength = 0;
        var qualifying = new List<ResponseItem>();
        foreach (var item in items)
        {
            if (!item.IsToolOutput)
                continue;

            seen++;
            var allowed = item.IsIntrinsicShellOutput
                || (item.CallId is not null
                    && toolNames.TryGetValue(item.CallId, out var name)
                    && IsAllowed(toolAllowlist, name));
            if (!allowed || item.Ranges.Count == 0)
                continue;

            qualifying.Add(item);
            foreach (var (start, end) in item.Ranges)
                maxTokenLength = Math.Max(maxTokenLength, end - start);
        }

        if (qualifying.Count == 0)
            return unchanged with { ToolResultsSeen = seen };

        var unescaped = ArrayPool<char>.Shared.Rent(maxTokenLength);
        var minified = ArrayPool<char>.Shared.Rent(maxTokenLength);
        var condensed = ArrayPool<char>.Shared.Rent(maxTokenLength);
        using var scratch = new PooledBufferWriter(maxTokenLength);
        using var writer = new Utf8JsonWriter(scratch, StringWriterOptions);
        try
        {
            var cursor = 0;
            var bytesWritten = 0;
            var minifiedResults = 0;
            var anyChanged = false;

            foreach (var item in qualifying)
            {
                var itemChanged = false;
                foreach (var (start, end) in item.Ranges)
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
                                itemChanged = true;
                                anyChanged = true;
                            }
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
                    {
                        accepted = false;
                    }

                    if (!accepted)
                    {
                        output.Write(token);
                        bytesWritten += token.Length;
                    }

                    cursor = end;
                }

                if (itemChanged)
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

    private sealed class ResponseItem
    {
        public string? Type;
        public string? Name;
        public string? CallId;
        public readonly List<(int Start, int End)> Ranges = [];

        public bool IsToolCall => Type is "function_call" or "custom_tool_call";
        public bool IsToolOutput => Type is
            "function_call_output" or
            "custom_tool_call_output" or
            "local_shell_call_output" or
            "shell_call_output";
        public bool IsIntrinsicShellOutput => Type is "local_shell_call_output" or "shell_call_output";
    }

    private static List<ResponseItem> LocateResponseItems(ReadOnlySpan<byte> utf8Body)
    {
        var items = new List<ResponseItem>();
        var reader = new Utf8JsonReader(utf8Body);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return items;

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.CurrentDepth == 1 && reader.ValueTextEquals("input"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartArray)
                    ScanInput(ref reader, items);
                else
                    reader.Skip();
            }
            else
            {
                reader.Skip();
            }
        }

        return items;
    }

    private static void ScanInput(ref Utf8JsonReader reader, List<ResponseItem> items)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
                items.Add(ScanItem(ref reader));
            else
                reader.Skip();
        }
    }

    private static ResponseItem ScanItem(ref Utf8JsonReader reader)
    {
        var item = new ResponseItem();
        var directOutputRanges = new List<(int Start, int End)>();
        var contentOutputRanges = new List<(int Start, int End)>();
        var shellOutputRanges = new List<(int Start, int End)>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (reader.ValueTextEquals("type"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                    item.Type = reader.GetString();
                else
                    reader.Skip();
            }
            else if (reader.ValueTextEquals("name"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                    item.Name = reader.GetString();
                else
                    reader.Skip();
            }
            else if (reader.ValueTextEquals("call_id"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                    item.CallId = reader.GetString();
                else
                    reader.Skip();
            }
            else if (reader.ValueTextEquals("output"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                {
                    directOutputRanges.Add(CurrentStringTokenRange(ref reader));
                }
                else if (reader.TokenType == JsonTokenType.StartArray)
                {
                    ScanOutputArray(ref reader, contentOutputRanges, shellOutputRanges);
                }
                else
                {
                    reader.Skip();
                }
            }
            else
            {
                reader.Skip();
            }
        }

        if (item.Type is "function_call_output" or "custom_tool_call_output" or "local_shell_call_output")
        {
            item.Ranges.AddRange(directOutputRanges);
            item.Ranges.AddRange(contentOutputRanges);
        }
        else if (item.Type == "shell_call_output")
        {
            item.Ranges.AddRange(shellOutputRanges);
        }

        item.Ranges.Sort(static (left, right) => left.Start.CompareTo(right.Start));
        return item;
    }

    private static void ScanOutputArray(
        ref Utf8JsonReader reader,
        List<(int Start, int End)> contentRanges,
        List<(int Start, int End)> shellRanges)
    {
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            string? type = null;
            (int Start, int End)? text = null;
            (int Start, int End)? stdout = null;
            (int Start, int End)? stderr = null;
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                if (reader.TokenType != JsonTokenType.PropertyName)
                    continue;

                if (reader.ValueTextEquals("type"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                        type = reader.GetString();
                    else
                        reader.Skip();
                }
                else if (reader.ValueTextEquals("text"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                        text = CurrentStringTokenRange(ref reader);
                    else
                        reader.Skip();
                }
                else if (reader.ValueTextEquals("stdout"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                        stdout = CurrentStringTokenRange(ref reader);
                    else
                        reader.Skip();
                }
                else if (reader.ValueTextEquals("stderr"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String)
                        stderr = CurrentStringTokenRange(ref reader);
                    else
                        reader.Skip();
                }
                else
                {
                    reader.Skip();
                }
            }

            if (type == "input_text" && text is { } textRange)
                contentRanges.Add(textRange);
            if (stdout is { } stdoutRange)
                shellRanges.Add(stdoutRange);
            if (stderr is { } stderrRange)
                shellRanges.Add(stderrRange);
        }
    }

    private static (int Start, int End) CurrentStringTokenRange(ref Utf8JsonReader reader) =>
        ((int)reader.TokenStartIndex, (int)reader.BytesConsumed);
}
