using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using TokenSaver.Pipeline;
using TokenSaver.Shape;

namespace TokenSaver.Minify;

/// <summary>
/// Rewrites shell-tool outputs in an OpenAI Responses API request body. Codex represents its
/// conversation as <c>input[]</c> items: a tool call carries <c>name</c> + <c>call_id</c>, and the
/// corresponding output carries the same <c>call_id</c>. This rewriter correlates those items and
/// raw-byte-splices only output strings belonging to an allowlisted shell tool. When diagnostic
/// capture is enabled, recognized non-allowlisted outputs are recorded unchanged without becoming
/// eligible for rewriting.
///
/// Both ordinary <c>function_call(_output)</c> and <c>custom_tool_call(_output)</c> pairs are
/// supported, including array-form output content containing <c>input_text</c>. Provider-native
/// <c>local_shell_call_output</c> and <c>shell_call_output</c> items are intrinsically shell output;
/// when their paired call is present, correlation also recovers <c>action.commands</c> for safe
/// file-read budgeting. Every other byte remains exactly as Codex serialized it.
/// </summary>
public static class CodexResponsesRewriter
{
    /// <summary>
    /// Shell-output tool names used by current and earlier Codex clients, including code-mode
    /// exec and background wait (allowlisted 2026-08-16, plan_1A). Unknown tools fail toward
    /// no savings: their outputs are observed in the seen counter but never rewritten. The live
    /// path resolves its allowlist from <see cref="CompressionCatalog"/>; this list only backs
    /// the legacy overload and mirrors the catalog's Codex union.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultToolAllowlist =
        ["shell_command", "exec_command", "exec", "write_stdin", "wait"];

    private static readonly JsonWriterOptions StringWriterOptions = new()
    {
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
            CompressionPlan.FromLegacy(flags, condense, codexAllowlist: [.. toolAllowlist]),
            output);

    /// <summary>Rewrites with one immutable request plan, the sole runtime configuration value.</summary>
    /// <param name="provider">Stable provider tag stored with optional captures — "openai" for
    /// Codex itself; the Grok cli-chat route passes its own tag.</param>
    public static ToolOutputRewriteResult Rewrite(
        ReadOnlySpan<byte> utf8Body,
        CompressionPlan plan,
        IBufferWriter<byte> output,
        ICompressionCaptureSink? captures = null,
        string provider = "openai")
    {
        ArgumentNullException.ThrowIfNull(plan);
        var stats = new MinifyStats();
        var condenseStats = new CondenseStats();
        var unchanged = new ToolOutputRewriteResult(
            false, utf8Body.Length, utf8Body.Length, 0, 0, stats);
        var toolAllowlist = plan.CodexAllowlist;
        if (utf8Body.IsEmpty
            || (captures is null && (plan.IsNoOp || toolAllowlist.Count == 0)))
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

        var toolUses = new Dictionary<string, ToolUseInfo>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (item.CallId is null || !item.IsToolCall)
                continue;

            // Provider-native shell calls have no `name`; their type is the identity. Keep them in
            // the same call-id map as function/custom tools so shell_call.action.commands reaches
            // the paired shell_call_output instead of being discarded as unrelated metadata.
            var name = item.Name ?? (item.IsIntrinsicShellCall ? "shell_command" : null);
            if (name is not null)
                toolUses[item.CallId] = new ToolUseInfo(name, item.Command);
        }

        var seen = 0;
        var maxTokenLength = 0;
        var qualifying = new List<ResponseItem>();
        foreach (var item in items)
        {
            if (!item.IsToolOutput)
                continue;

            seen++;
            if (item.Ranges.Count == 0)
                continue;

            if (item.CallId is not null && toolUses.TryGetValue(item.CallId, out var use))
            {
                item.Name = use.Name;
                item.Command = use.Command;
                item.PreserveFileContentsWhenCommandUnknown =
                    item.IsIntrinsicShellOutput && use.Command is null;
            }
            else if (item.IsIntrinsicShellOutput)
            {
                // A client may send only shell_call_output plus previous_response_id, leaving the
                // producing shell_call server-side. We still know this is shell output but cannot
                // prove whether it was a file read. The detector's safety polarity requires the
                // conservative answer: a false positive only keeps more context, while false would
                // reopen source-file truncation on this supported wire form.
                item.Name = "shell_command";
                item.Command = null;
                item.PreserveFileContentsWhenCommandUnknown = true;
            }
            else
            {
                // A recognized output shape without a correlated call has no trustworthy tool
                // identity. It remains counted, but cannot be compressed or captured honestly.
                continue;
            }

            if (!plan.IsNoOp && IsAllowed(toolAllowlist, item.Name))
                qualifying.Add(item);

            foreach (var (start, end) in item.Ranges)
                maxTokenLength = Math.Max(maxTokenLength, end - start);
        }

        if (qualifying.Count == 0 && captures is null)
            return unchanged with { ToolResultsSeen = seen };

        var unescaped = ArrayPool<char>.Shared.Rent(Math.Max(maxTokenLength, 1));
        try
        {
            if (captures is not null)
            {
                CaptureDiagnosticOnly(
                    utf8Body,
                    items,
                    toolAllowlist,
                    plan,
                    captures,
                    unescaped,
                    provider);
            }

            if (qualifying.Count == 0)
                return unchanged with { ToolResultsSeen = seen };

            using var buffers = new PipelineScratch(maxTokenLength);
            using var scratch = new PooledBufferWriter(maxTokenLength);
            using var writer = new Utf8JsonWriter(scratch, StringWriterOptions);
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

                        var raw = (ReadOnlySpan<char>)unescaped.AsSpan(0, charLength);
                        var trace = captures is not null ? new List<StageTrace>() : null;
                        var current = CompressionPipeline.Run(
                            raw,
                            plan,
                            CommandShapes.Classify(item.Command),
                            item.PreserveFileContentsWhenCommandUnknown
                                || CommandShapes.ReadsFileContents(item.Command),
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
                                itemChanged = true;
                                anyChanged = true;
                            }
                        }

                        if (trace is not null)
                        {
                            captures!.Capture(new CompressionCapture(
                                Guid.NewGuid(),
                                provider,
                                item.Name ?? "shell_command",
                                item.Command,
                                raw.ToString(),
                                current.ToString(),
                                trace,
                                [.. plan.EnabledIds],
                                RewriteAccepted: accepted));
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
        }
    }

    private readonly record struct ToolUseInfo(string Name, string? Command);

    private static void CaptureDiagnosticOnly(
        ReadOnlySpan<byte> utf8Body,
        IReadOnlyList<ResponseItem> items,
        IReadOnlyCollection<string> toolAllowlist,
        CompressionPlan plan,
        ICompressionCaptureSink captures,
        Span<char> unescaped,
        string provider)
    {
        foreach (var item in items)
        {
            if (!item.IsToolOutput
                || item.Ranges.Count == 0
                || item.Name is null
                || (!plan.IsNoOp && IsAllowed(toolAllowlist, item.Name)))
            {
                continue;
            }

            foreach (var (start, end) in item.Ranges)
            {
                try
                {
                    var tokenReader = new Utf8JsonReader(utf8Body[start..end]);
                    tokenReader.Read();
                    var charLength = tokenReader.CopyString(unescaped);
                    var raw = unescaped[..charLength].ToString();

                    captures.Capture(new CompressionCapture(
                        Guid.NewGuid(),
                        provider,
                        item.Name!,
                        item.Command,
                        raw,
                        raw,
                        [],
                        [.. plan.EnabledIds],
                        RewriteAccepted: false));
                }
                catch (Exception ex) when (
                    ex is JsonException or InvalidOperationException or ArgumentException)
                {
                    // Per-string fail-open: malformed/untranscodable text is neither captured nor
                    // rewritten, and must not suppress diagnostics for the remaining ranges.
                }
            }
        }
    }

    private static bool IsAllowed(IReadOnlyCollection<string> allowlist, string? name)
    {
        if (name is null)
            return false;

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
        public string? Command;
        public bool PreserveFileContentsWhenCommandUnknown;
        public readonly List<(int Start, int End)> Ranges = [];

        public bool IsToolCall => Type is
            "function_call" or
            "custom_tool_call" or
            "local_shell_call" or
            "shell_call";
        public bool IsToolOutput => Type is
            "function_call_output" or
            "custom_tool_call_output" or
            "local_shell_call_output" or
            "shell_call_output";
        public bool IsIntrinsicShellCall => Type is "local_shell_call" or "shell_call";
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
            else if (reader.ValueTextEquals("arguments"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                    item.Command = ExtractCommand(reader.GetString());
                else
                    reader.Skip();
            }
            else if (reader.ValueTextEquals("input"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.String)
                    item.Command = reader.GetString();
                else
                    reader.Skip();
            }
            else if (reader.ValueTextEquals("action"u8))
            {
                reader.Read();
                if (reader.TokenType == JsonTokenType.StartObject)
                    item.Command = ReadShellCommandsFromAction(ref reader);
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

    /// <summary>
    /// Reads the current Responses shell tool shape: <c>shell_call.action.commands</c>. Commands
    /// are joined with newlines because the file-read detector deliberately scans compound command
    /// text, while the stricter shape classifier will correctly decline the compound form.
    /// </summary>
    private static string? ReadShellCommandsFromAction(ref Utf8JsonReader reader)
    {
        List<string>? commands = null;
        string? singleCommand = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
                continue;

            if (!reader.ValueTextEquals("commands"u8))
            {
                reader.Skip();
                continue;
            }

            reader.Read();
            if (reader.TokenType == JsonTokenType.String)
            {
                singleCommand = reader.GetString();
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                reader.Skip();
                continue;
            }

            commands = [];
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType == JsonTokenType.String)
                    commands.Add(reader.GetString()!);
                else
                    reader.Skip();
            }
        }

        return commands is { Count: > 0 } ? string.Join('\n', commands) : singleCommand;
    }

    private static string? ExtractCommand(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return null;

        try
        {
            using var document = JsonDocument.Parse(arguments);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("command", out var command)
                && command.ValueKind == JsonValueKind.String
                    ? command.GetString()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
