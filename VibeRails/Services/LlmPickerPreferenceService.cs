using System.Text.Json;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services;

public interface ILlmPickerPreferenceService
{
    Task<LlmPickerPreferencesResponse> GetAsync(CancellationToken cancellationToken = default);
    Task<LlmPickerPreferencesResponse> SaveAsync(
        UpdateLlmPickerPreferencesRequest request,
        CancellationToken cancellationToken = default);
    Task<LlmPickerPreferencesResponse> ResetAsync(CancellationToken cancellationToken = default);
    /// <summary>Reads this computer's saved launch choices for a base LLM.</summary>
    Task<BaseLlmOptions> GetLaunchDefaultsAsync(string cli, CancellationToken cancellationToken = default);
    /// <summary>Saves validated launch choices for one base LLM without changing CLI configuration.</summary>
    Task<BaseLlmOptions> SaveLaunchDefaultsAsync(string cli, BaseLlmOptions options, CancellationToken cancellationToken = default);
}

public sealed class LlmPickerPreferenceValidationException(string message) : Exception(message);

public sealed partial class LlmPickerPreferenceService(IRepository repository, VibeRails.Launches.ILaunchStore? launches = null) : ILlmPickerPreferenceService
{
    public const string CacheKey = "ui.llm-picker.launches.v1";
    internal const int DocumentVersion = 1;
    private const int MaxItems = 1000;
    private const string BaseGroup = "Base CLIs";
    private const string EnvironmentGroup = "AI Agents";

    private static readonly BasePickerItem[] CanonicalBaseItems =
    [
        new("claude", "Claude"),
        new("codex", "Codex"),
        new("glm-5.2", "GLM 5.2"),
        new("glm-5.3", "GLM 5.3"),
        new("deepseek-v4-pro", "DeepSeek V4 Pro"),
        new("kimi-k3", "Kimi K3"),
        new("grok", "Grok"),
        new("opencode", "OpenCode"),
        new("copilot", "Copilot"),
        new("antigravity", "Antigravity"),
        new("shell", "Terminal")
    ];

    private readonly IRepository _repository = repository;

    public async Task<LlmPickerPreferencesResponse> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var environments = launches is null ? [] : await launches.ListPresetsAsync(cancellationToken);
        var documentJson = await _repository.GetGlobalCacheValueAsync(CacheKey, cancellationToken);
        var document = DeserializeDocument(documentJson);
        return Resolve(environments, document);
    }

    public async Task<LlmPickerPreferencesResponse> SaveAsync(
        UpdateLlmPickerPreferencesRequest request,
        CancellationToken cancellationToken = default)
    {
        var environments = launches is null ? [] : await launches.ListPresetsAsync(cancellationToken);
        var currentCatalog = Resolve(environments, document: null).Items;
        var normalized = ValidateAndNormalize(request.Items, currentCatalog);

        var baseItems = normalized
            .Where(item => item.Kind == "base")
            .OrderBy(item => item.Order)
            .ToList();
        var environmentItems = normalized
            .Where(item => item.Kind != "base")
            .OrderBy(item => item.Order)
            .ToList();

        var document = new LlmPickerPreferenceDocument(
            DocumentVersion,
            baseItems.Select(item => item.Key).ToList(),
            environmentItems.Select(item => item.Key).ToList(),
            normalized.Where(item => !item.Enabled).Select(item => item.Key).ToList());
        var documentJson = JsonSerializer.Serialize(
            document,
            AppJsonSerializerContext.Default.LlmPickerPreferenceDocument);
        await _repository.SetGlobalCacheValueAsync(CacheKey, documentJson, cancellationToken);

        // Resolve from the normalized snapshot rather than re-reading three pieces of
        // state. The repository transaction is the commit boundary, so this response is
        // exactly the state that was accepted and persisted.
        return new LlmPickerPreferencesResponse(normalized);
    }

    public async Task<LlmPickerPreferencesResponse> ResetAsync(
        CancellationToken cancellationToken = default)
    {
        var environments = launches is null ? [] : await launches.ListPresetsAsync(cancellationToken);
        await _repository.SetGlobalCacheValueAsync(CacheKey, "", cancellationToken);

        return Resolve(environments, document: null);
    }

    private static LlmPickerPreferencesResponse Resolve(
        IReadOnlyCollection<VibeRails.Launches.LaunchPreset> environments,
        LlmPickerPreferenceDocument? document)
    {
        var baseCatalog = CanonicalBaseItems
            .Select(item => new LlmPickerPreferenceItem(
                $"base:{item.Cli}",
                "base",
                BaseGroup,
                item.Label,
                item.Cli,
                null,
                Enabled: true,
                Order: 0))
            .ToList();

        var environmentCatalog = environments.Where(p => p.RetiredUtc is null)
            .Select(p => new LlmPickerPreferenceItem(
                $"preset:{p.Id}:{ToPickerCli(p.Cli)}", p.Kind, EnvironmentGroup, p.Name,
                ToPickerCli(p.Cli), null, Enabled: p.Enabled, Order: 0)).ToList();

        var disabledBaseKeys = document?.Version == DocumentVersion
            ? document.DisabledBaseKeys.Select(CanonicalizeSavedKey).ToHashSet(StringComparer.Ordinal)
            : [];
        baseCatalog = ApplySavedOrder(
            baseCatalog,
            document?.Version == DocumentVersion ? document.BaseOrder : null)
            .Select((item, index) => item with
            {
                Enabled = !disabledBaseKeys.Contains(item.Key),
                Order = index
            })
            .ToList();
        environmentCatalog = ApplySavedOrder(
            environmentCatalog,
            document?.Version == DocumentVersion ? document.EnvironmentOrder : null)
            .Select((item, index) => item with { Order = index, Enabled = item.Enabled && !disabledBaseKeys.Contains(item.Key) })
            .ToList();

        return new LlmPickerPreferencesResponse([.. baseCatalog, .. environmentCatalog]);
    }

    private static List<LlmPickerPreferenceItem> ApplySavedOrder(
        IReadOnlyCollection<LlmPickerPreferenceItem> catalog,
        IReadOnlyCollection<string>? savedOrder)
    {
        if (savedOrder == null || savedOrder.Count == 0)
        {
            return [.. catalog];
        }

        var byKey = catalog.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var ordered = new List<LlmPickerPreferenceItem>(catalog.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in savedOrder)
        {
            var canonical = CanonicalizeSavedKey(key);
            if (seen.Add(canonical) && byKey.TryGetValue(canonical, out var item))
            {
                ordered.Add(item);
            }
        }

        ordered.AddRange(catalog.Where(item => seen.Add(item.Key)));
        return ordered;
    }

    private static List<LlmPickerPreferenceItem> ValidateAndNormalize(
        List<LlmPickerPreferenceItem>? submitted,
        IReadOnlyCollection<LlmPickerPreferenceItem> currentCatalog)
    {
        if (submitted == null)
        {
            throw new LlmPickerPreferenceValidationException("Items are required.");
        }

        if (submitted.Count > MaxItems)
        {
            throw new LlmPickerPreferenceValidationException(
                $"A maximum of {MaxItems} picker items can be saved.");
        }

        var expectedByKey = currentCatalog.ToDictionary(item => item.Key, StringComparer.Ordinal);
        if (submitted.Count != expectedByKey.Count)
        {
            throw new LlmPickerPreferenceValidationException(
                "The picker snapshot must contain every current catalog item exactly once.");
        }

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenPositions = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal)
        {
            [BaseGroup] = [],
            [EnvironmentGroup] = []
        };

        foreach (var item in submitted)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || !seenKeys.Add(item.Key))
            {
                throw new LlmPickerPreferenceValidationException(
                    $"Duplicate or malformed picker key: '{item.Key}'.");
            }

            if (!expectedByKey.TryGetValue(item.Key, out var expected))
            {
                throw new LlmPickerPreferenceValidationException(
                    $"Unknown picker item: '{item.Key}'.");
            }

            if (!string.Equals(item.Kind, expected.Kind, StringComparison.Ordinal)
                || !string.Equals(item.Group, expected.Group, StringComparison.Ordinal)
                || !string.Equals(item.Label, expected.Label, StringComparison.Ordinal)
                || !string.Equals(item.Cli, expected.Cli, StringComparison.Ordinal)
                || item.EnvironmentId != expected.EnvironmentId)
            {
                throw new LlmPickerPreferenceValidationException(
                    $"Picker item '{item.Key}' does not match the current catalog.");
            }

            if (item.Order < 0 || !seenPositions[item.Group].Add(item.Order))
            {
                throw new LlmPickerPreferenceValidationException(
                    $"Picker group '{item.Group}' contains a duplicate or invalid position.");
            }
        }

        if (seenKeys.Count != expectedByKey.Count)
        {
            throw new LlmPickerPreferenceValidationException(
                "The picker snapshot is missing one or more current catalog items.");
        }

        var normalized = new List<LlmPickerPreferenceItem>(submitted.Count);
        foreach (var group in new[] { BaseGroup, EnvironmentGroup })
        {
            normalized.AddRange(submitted
                .Where(item => item.Group == group)
                .OrderBy(item => item.Order)
                .Select((item, index) => item with { Order = index }));
        }

        return normalized;
    }

    private static LlmPickerPreferenceDocument? DeserializeDocument(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize(
                json,
                AppJsonSerializerContext.Default.LlmPickerPreferenceDocument);
            if (document?.Version != DocumentVersion
                || document.BaseOrder == null
                || document.EnvironmentOrder == null
                || document.DisabledBaseKeys == null
                || document.BaseOrder.Any(string.IsNullOrWhiteSpace)
                || document.EnvironmentOrder.Any(string.IsNullOrWhiteSpace)
                || document.DisabledBaseKeys.Any(string.IsNullOrWhiteSpace))
            {
                return null;
            }

            return document;
        }
        catch (JsonException)
        {
            // A corrupt/legacy cache value must never prevent launch controls from
            // rendering. The next successful save replaces it with a valid document.
            return null;
        }
    }

    private static string ToPickerCli(LLM llm) =>
        LlmParser.ToWireName(llm).ToLowerInvariant();

    // The Grok CLI used to be stored as grok-4.6. Saved order and hidden keys from that
    // wire name still refer to the same catalog row.
    private static string CanonicalizeSavedKey(string key)
    {
        if (key.Equals("base:grok-4.6", StringComparison.OrdinalIgnoreCase))
            return "base:grok";

        const string suffix = ":grok-4.6";
        if (key.StartsWith("env:", StringComparison.OrdinalIgnoreCase)
            && key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            return string.Concat(key.AsSpan(0, key.Length - suffix.Length), ":grok");
        }

        return key;
    }

    private sealed record BasePickerItem(string Cli, string Label);
}
