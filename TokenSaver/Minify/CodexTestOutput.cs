using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TokenSaver.Pipeline;
using TokenSaver.Shape;

namespace TokenSaver.Minify;

/// <summary>
/// Decodes only known exec result envelopes. Each output string goes through the ordinary
/// pipeline; metadata and all surrounding JSON are byte-spliced unchanged. No recursive search
/// for arbitrary "output" properties, and no serialized-JSON line filtering.
/// </summary>
internal static class CodexTestOutput
{
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    internal static bool TryRewrite(ReadOnlySpan<char> raw, CompressionPlan plan, PipelineScratch scratch,
        out string rewritten, ref MinifyStats stats, ref CondenseStats condense,
        ICollection<StageTrace>? trace)
    {
        rewritten = raw.ToString();
        try
        {
            var bytes = StrictUtf8.GetBytes(rewritten);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            if (!IsEnvelope(document.RootElement, 0)) return false;
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = 16 });
            var output = new ArrayBufferWriter<byte>();
            var tokenBuffer = new ArrayBufferWriter<byte>();
            using var writer = new Utf8JsonWriter(tokenBuffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            var cursor = 0;
            var candidateStats = stats;
            var candidateCondense = condense;
            var traces = trace is null ? null : new List<StageTrace>();
            var found = false;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || !reader.ValueTextEquals("output"u8)) continue;
                reader.Read(); // IsEnvelope proved this is a string belonging to an exec result.
                var start = (int)reader.TokenStartIndex;
                var end = (int)reader.BytesConsumed;
                var text = reader.GetString()!;
                var leafStats = candidateStats;
                var leafCondense = candidateCondense;
                var leafTrace = trace is null ? null : new List<StageTrace>();
                var current = CompressionPipeline.Run(text, plan, CommandShape.TestRun, false, scratch,
                    out var changed, ref leafStats, ref leafCondense, leafTrace);
                output.Write(bytes.AsSpan(cursor, start - cursor));
                tokenBuffer.Clear();
                writer.Reset(tokenBuffer);
                writer.WriteStringValue(current);
                writer.Flush();
                if (changed && tokenBuffer.WrittenCount < end - start)
                {
                    output.Write(tokenBuffer.WrittenSpan);
                    candidateStats = leafStats;
                    candidateCondense = leafCondense;
                    if (leafTrace is not null) traces!.AddRange(leafTrace);
                }
                else
                {
                    output.Write(bytes.AsSpan(start, end - start));
                    if (leafTrace is not null)
                        traces!.AddRange(leafTrace.Select(x => x.Outcome == StageOutcome.Applied
                            ? x with { Outcome = StageOutcome.NoChange, CharsRemoved = 0 } : x));
                }
                cursor = end;
                found = true;
            }
            if (!found) return false;
            output.Write(bytes.AsSpan(cursor));
            var candidate = StrictUtf8.GetString(output.WrittenSpan);
            // Both levels enforce never-grows. The caller also checks the outer request token.
            if (output.WrittenCount < bytes.Length && candidate.Length < raw.Length)
            {
                rewritten = candidate;
                stats = candidateStats;
                condense = candidateCondense;
            }
            if (traces is not null)
            {
                foreach (var group in traces.GroupBy(x => x.StageId))
                {
                    var removed = group.Sum(x => x.CharsRemoved);
                    trace!.Add(new StageTrace(group.Key, removed > 0 ? StageOutcome.Applied
                        : group.Any(x => x.Outcome == StageOutcome.Aborted) ? StageOutcome.Aborted
                        : group.First().Outcome, removed));
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool IsEnvelope(JsonElement element, int depth)
    {
        if (depth > 4) return false;
        if (element.ValueKind == JsonValueKind.Array)
            return element.GetArrayLength() is > 0 and <= 32 && element.EnumerateArray().All(x => IsEnvelope(x, depth + 1));
        if (element.ValueKind != JsonValueKind.Object) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!keys.Add(property.Name)) return false;
        if (element.TryGetProperty("status", out var status))
            return keys.SetEquals(["status", "value"]) && status.ValueKind == JsonValueKind.String
                && status.GetString() == "fulfilled" && IsEnvelope(element.GetProperty("value"), depth + 1);
        if (!element.TryGetProperty("output", out var text) || text.ValueKind != JsonValueKind.String) return false;
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name is not ("output" or "chunk_id" or "wall_time_seconds" or "exit_code"
                or "original_token_count" or "session_id")) return false;
            if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object) return false;
        }
        return true;
    }
}
