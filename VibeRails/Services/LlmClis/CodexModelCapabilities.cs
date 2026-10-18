using System.Text.Json;

namespace VibeRails.Services.LlmClis;

// The browser imports the same JSON that is embedded here for AOT and stdio launches.
// Speed tiers verified against Codex's model metadata and speed guide, 2026-10-17.
// Only Astra and GPT-6.1 Sol are pinned (owner request, 2026-10-17).
internal static class CodexModelCapabilities
{
    private static readonly IReadOnlyDictionary<string, string[]> Speeds = LoadSpeeds();

    public static IEnumerable<string> Models => Speeds.Keys;

    public static bool SupportsSpeed(string? model, string speed) =>
        Speeds.TryGetValue(model?.Trim() ?? "", out var speeds) && speeds.Contains(speed);

    private static IReadOnlyDictionary<string, string[]> LoadSpeeds()
    {
        using var stream = typeof(CodexModelCapabilities).Assembly.GetManifestResourceStream("CodexModelCapabilities.json")
            ?? throw new InvalidOperationException("The Codex model capability resource is missing.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateObject().ToDictionary(entry => entry.Name,
            entry => entry.Value.EnumerateArray().Select(value => value.GetString()!).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    }
}
