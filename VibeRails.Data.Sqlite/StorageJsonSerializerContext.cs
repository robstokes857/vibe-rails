using System.Text.Json.Serialization;
using VibeRails.DTOs;
namespace VibeRails.DTOs;
[JsonSerializable(typeof(VibeRails.Services.Board.BoardReviewRecord))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(VibeRails.Services.Board.BoardCheckRecord))]
[JsonSerializable(typeof(SandboxDiffResponse))]
[JsonSerializable(typeof(BaseLlmOptions))]
[JsonSerializable(typeof(VibeRails.Services.Board.BoardContextSettings))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class StorageJsonSerializerContext : JsonSerializerContext;
