using System.Text.Json.Serialization;

namespace PyBridge;

/// <summary>
/// What an interpreter reports about itself via <c>ProbeAsync</c> — the ground truth of
/// which Python you are actually talking to (not which one you hoped was on PATH).
/// </summary>
public sealed record PythonInfo
{
    /// <summary>The Python version, e.g. <c>3.12.7</c>.</summary>
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    /// <summary>Full path of the interpreter binary that actually ran.</summary>
    [JsonPropertyName("executable")]
    public required string Executable { get; init; }

    /// <summary>The installation prefix (<c>sys.prefix</c>) — for a venv, the venv directory.</summary>
    [JsonPropertyName("prefix")]
    public required string Prefix { get; init; }

    /// <summary>The implementation name, almost always <c>CPython</c>.</summary>
    [JsonPropertyName("implementation")]
    public required string Implementation { get; init; }
}

/// <summary>
/// The library's own source-generated JSON context — serializer metadata is emitted at
/// compile time, which is what keeps JSON handling AOT/trim safe with zero reflection.
/// </summary>
[JsonSerializable(typeof(PythonInfo))]
internal sealed partial class PyBridgeJsonContext : JsonSerializerContext;
