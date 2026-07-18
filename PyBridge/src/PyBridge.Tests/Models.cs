using System.Text.Json;
using System.Text.Json.Serialization;

namespace PyBridge.Tests;

/// <summary>One request line sent to <c>python/worker.py</c> (see its docstring for the ops).</summary>
public sealed class WorkerRequest
{
    [JsonPropertyName("op")] public string? Op { get; set; }
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("a")] public int? A { get; set; }
    [JsonPropertyName("b")] public int? B { get; set; }
}

/// <summary>One reply line from the worker: <c>{"ok": ..., "result": ...}</c> or <c>{"ok": false, "error": ...}</c>.</summary>
public sealed class WorkerReply
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("result")] public JsonElement Result { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>
/// Source-generated JSON context. This is what makes JSON parsing AOT/trim safe:
/// the serializer metadata is generated at compile time, so no runtime reflection is needed.
/// Nulls are skipped when writing so <c>{"op":"ping"}</c> stays exactly that.
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkerRequest))]
[JsonSerializable(typeof(WorkerReply))]
public partial class WorkerJsonContext : JsonSerializerContext;
