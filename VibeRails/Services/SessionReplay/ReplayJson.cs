using System.Text.Json.Serialization;
using VibeRails.Data.Replay;

namespace VibeRails.Services.SessionReplay;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionPage))]
[JsonSerializable(typeof(Manifest))]
[JsonSerializable(typeof(FramePage))]
[JsonSerializable(typeof(ExchangePage))]
[JsonSerializable(typeof(ExchangeDetail))]
[JsonSerializable(typeof(DiffDetail))]
[JsonSerializable(typeof(AppStatus))]
[JsonSerializable(typeof(ApiError))]
public partial class ReplayJson : JsonSerializerContext;
