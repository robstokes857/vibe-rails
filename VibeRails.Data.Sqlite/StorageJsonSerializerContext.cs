using System.Text.Json.Serialization;
using VibeRails.DTOs;
namespace VibeRails.DTOs;
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(SandboxDiffResponse))]
[JsonSerializable(typeof(BaseLlmOptions))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class StorageJsonSerializerContext : JsonSerializerContext;
