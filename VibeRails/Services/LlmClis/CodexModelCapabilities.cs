using System.Text.Json;

namespace VibeRails.Services.LlmClis;

// The browser imports the same JSON that is embedded here for AOT and stdio launches.
// Model, speed and effort metadata verified against Codex CLI 0.164.0, 2026-10-25.
// GPT-6.1 Terra: refreshed Codex model cache and official speed docs, 2026-10-26.
internal static class CodexModelCapabilities
{
    private sealed record Capability(string[] Speeds, string[] Efforts);
    private static readonly IReadOnlyDictionary<string, Capability> Catalog = Load();

    public static IEnumerable<string> Models => Catalog.Keys;

    public static bool SupportsSpeed(string? model, string speed) =>
        Catalog.TryGetValue(model?.Trim() ?? "", out var capability) && capability.Speeds.Contains(speed);

    public static IEnumerable<string> Efforts(string model) => Catalog[model].Efforts;

    public static string NormalizeEffort(string? model, string effort)
    {
        if (Catalog.TryGetValue(model?.Trim() ?? "", out var capability))
        {
            if (effort == "minimal") return capability.Efforts[0];
            if (effort == "ultra" && !capability.Efforts.Contains(effort)) return capability.Efforts[^1];
        }
        return string.Equals(model?.Trim(), "gpt-5.5", StringComparison.OrdinalIgnoreCase) && effort == "max"
            ? "xhigh" : effort;
    }

    private static IReadOnlyDictionary<string, Capability> Load()
    {
        using var stream = typeof(CodexModelCapabilities).Assembly.GetManifestResourceStream("CodexModelCapabilities.json")
            ?? throw new InvalidOperationException("The Codex model capability resource is missing.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.EnumerateObject().ToDictionary(entry => entry.Name,
            entry => new Capability(
                entry.Value.GetProperty("speeds").EnumerateArray().Select(value => value.GetString()!).ToArray(),
                entry.Value.GetProperty("efforts").EnumerateArray().Select(value => value.GetString()!).ToArray()),
            StringComparer.OrdinalIgnoreCase);
    }
}
