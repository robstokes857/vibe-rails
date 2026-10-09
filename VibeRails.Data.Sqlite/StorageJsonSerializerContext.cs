using System.Text.Json.Serialization;
using VibeRails.DTOs;
namespace VibeRails.DTOs;
[JsonSerializable(typeof(VibeRails.Services.Board.BoardHandoff))]
[JsonSerializable(typeof(float[]))]
[JsonSerializable(typeof(VibeRails.Services.BertV2.SearchDocument))]
[JsonSerializable(typeof(VibeRails.Services.Board.BoardReviewRecord))]
[JsonSerializable(typeof(ReviewerRouting))]
[JsonSerializable(typeof(BoardReviewSettings))]
[JsonSerializable(typeof(ReviewLaunchSnapshot))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, long>))]
[JsonSerializable(typeof(VibeRails.Services.Board.BoardCheckRecord))]
[JsonSerializable(typeof(SandboxDiffResponse))]
[JsonSerializable(typeof(BaseLlmOptions))]
[JsonSerializable(typeof(VibeRails.Services.Board.BoardContextSettings))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class StorageJsonSerializerContext : JsonSerializerContext;
