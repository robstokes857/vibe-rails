using System.Text.Json.Serialization;

namespace PyBridge.Console;

/// <summary>Mirror of the JSON printed by <c>classifier.py check</c>.</summary>
public sealed class CheckReport
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("python_version")] public string? PythonVersion { get; set; }
    [JsonPropertyName("python_executable")] public string? PythonExecutable { get; set; }
    [JsonPropertyName("platform")] public string? Platform { get; set; }
    [JsonPropertyName("torch_available")] public bool TorchAvailable { get; set; }
    [JsonPropertyName("torchvision_available")] public bool TorchvisionAvailable { get; set; }
    [JsonPropertyName("torch_version")] public string? TorchVersion { get; set; }
    [JsonPropertyName("cuda_available")] public bool CudaAvailable { get; set; }
    [JsonPropertyName("gpu_name")] public string? GpuName { get; set; }
}

/// <summary>Mirror of the JSON printed by <c>classifier.py classify</c>.</summary>
public sealed class ClassifyResult
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("device")] public string? Device { get; set; }
    [JsonPropertyName("cuda_available")] public bool CudaAvailable { get; set; }
    [JsonPropertyName("synthetic_image")] public bool SyntheticImage { get; set; }
    [JsonPropertyName("inference_seconds")] public double InferenceSeconds { get; set; }
    [JsonPropertyName("predictions")] public List<Prediction>? Predictions { get; set; }
}

/// <summary>A single top-K prediction row.</summary>
public sealed class Prediction
{
    [JsonPropertyName("rank")] public int Rank { get; set; }
    [JsonPropertyName("label")] public string? Label { get; set; }
    [JsonPropertyName("class_index")] public int ClassIndex { get; set; }
    [JsonPropertyName("confidence")] public double Confidence { get; set; }
}

/// <summary>Typed request for worker.py's <c>add</c> op (DEMO 9's warm session).</summary>
public sealed class AddRequest
{
    [JsonPropertyName("op")] public string Op { get; set; } = "add";
    [JsonPropertyName("a")] public int A { get; set; }
    [JsonPropertyName("b")] public int B { get; set; }
}

/// <summary>worker.py's reply to <c>add</c> — a numeric result.</summary>
public sealed class AddReply
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("result")] public double Result { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>Typed request for worker.py's <c>upper</c> op.</summary>
public sealed class UpperRequest
{
    [JsonPropertyName("op")] public string Op { get; set; } = "upper";
    [JsonPropertyName("text")] public string? Text { get; set; }
}

/// <summary>worker.py's reply to <c>upper</c> — a text result.</summary>
public sealed class UpperReply
{
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("result")] public string? Result { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
}

/// <summary>
/// Source-generated JSON context. This is what makes JSON parsing AOT/trim safe:
/// the serializer metadata is generated at compile time, so no runtime reflection is needed.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(CheckReport))]
[JsonSerializable(typeof(ClassifyResult))]
[JsonSerializable(typeof(AddRequest))]
[JsonSerializable(typeof(AddReply))]
[JsonSerializable(typeof(UpperRequest))]
[JsonSerializable(typeof(UpperReply))]
public partial class PyJsonContext : JsonSerializerContext;
