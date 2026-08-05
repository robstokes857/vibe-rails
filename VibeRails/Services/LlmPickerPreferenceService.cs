using System.Text.Json;
using VibeRails.DB;
using VibeRails.DTOs;

namespace VibeRails.Services;

public interface ILlmPickerPreferenceService
{
    Task<LlmPickerPreferencesResponse> GetAsync(CancellationToken cancellationToken = default);
    Task<LlmPickerPreferencesResponse> SaveAsync(
        UpdateLlmPickerPreferencesRequest request,
        CancellationToken cancellationToken = default);
    Task<LlmPickerPreferencesResponse> ResetAsync(CancellationToken cancellationToken = default);
}

public sealed class LlmPickerPreferenceValidationException(string message) : Exception(message);

public sealed class LlmPickerPreferenceService(IRepository repository) : ILlmPickerPreferenceService
{
    public const string CacheKey = "ui.llm-picker.v1";
    internal const int DocumentVersion = 1;
    private const int MaxItems = 1000;
    private const string BaseGroup = "Base CLIs";
    private const string EnvironmentGroup = "Custom Environments";

    private static readonly BasePickerItem[] CanonicalBaseItems =
    [
        new("claude", "Claude"),
        new("codex", "Codex"),
        new("glm-5.2", "GLM 5.2"),
        new("opencode", "OpenCode"),
        new("copilot", "Copilot"),
        new("antigravity", "Antigravity"),
        new("shell", "Terminal")
    ];

    private readonly IRepository _repository = repository;

    public async Task<LlmPickerPreferencesResponse> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var environments = await _repository.GetCustomEnvironmentsAsync(cancellationToken);
        var documentJson = await _repository.GetGlobalCacheValueAsync(CacheKey, cancellationToken);
        var document = DeserializeDocument(documentJson);
        return Resolve(environments, document);
    }

    public async Task<LlmPickerPreferencesResponse> SaveAsync(
        UpdateLlmPickerPreferencesRequest request,
        CancellationToken cancellationToken = default)
    {
        var environments = await _repository.GetCustomEnvironmentsAsync(cancellationToken);
        var currentCatalog = Resolve(environments, document: null).Items;
        var normalized = ValidateAndNormalize(request.Items, currentCatalog);

        var baseItems = normalized
            .Where(item => item.Kind == "base")
            .OrderBy(item => item.Order)
            .ToList();
        var environmentItems = normalized
            .Where(item => item.Kind == "environment")
            .OrderBy(item => item.Order)
            .ToList();

        var document = new LlmPickerPreferenceDocument(
            DocumentVersion,
            baseItems.Select(item => item.Key).ToList(),
            environmentItems.Select(item => item.Key).ToList(),
            baseItems.Where(item => !item.Enabled).Select(item => item.Key).ToList());
        var documentJson = JsonSerializer.Serialize(
            document,
            AppJsonSerializerContext.Default.LlmPickerPreferenceDocument);
        var hiddenByEnvironmentId = environmentItems.ToDictionary(
            item => item.EnvironmentId!.Value,
            item => !item.Enabled);

        await _repository.SaveLlmPickerStateAsync(
            CacheKey,
            documentJson,
            hiddenByEnvironmentId,
            cancellationToken);

        // Resolve from the normalized snapshot rather than re-reading three pieces of
        // state. The repository transaction is the commit boundary, so this response is
        // exactly the state that was accepted and persisted.
        return new LlmPickerPreferencesResponse(normalized);
    }

    public async Task<LlmPickerPreferencesResponse> ResetAsync(
        CancellationToken cancellationToken = default)
    {
        var environments = await _repository.GetCustomEnvironmentsAsync(cancellationToken);
        var visibleEnvironmentState = environments
            .Where(IsSupportedCustomEnvironment)
            .ToDictionary(environment => environment.Id, _ => false);

        await _repository.SaveLlmPickerStateAsync(
            CacheKey,
            preferenceJson: null,
            visibleEnvironmentState,
            cancellationToken);

        foreach (var environment in environments)
        {
            environment.Hidden = false;
        }

        return Resolve(environments, document: null);
    }

    private static LlmPickerPreferencesResponse Resolve(
        IReadOnlyCollection<LLM_Environment> environments,
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

        var environmentCatalog = environments
            .Where(IsSupportedCustomEnvironment)
            .Select(environment =>
            {
                var cli = ToPickerCli(environment.LLM);
                return new LlmPickerPreferenceItem(
                    $"env:{environment.Id}:{cli}",
                    "environment",
                    EnvironmentGroup,
                    $"{environment.CustomName} ({cli})",
                    cli,
                    environment.Id,
                    Enabled: !environment.Hidden,
                    Order: 0);
            })
            .ToList();

        var disabledBaseKeys = document?.Version == DocumentVersion
            ? document.DisabledBaseKeys.ToHashSet(StringComparer.Ordinal)
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
            .Select((item, index) => item with { Order = index })
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
            if (seen.Add(key) && byKey.TryGetValue(key, out var item))
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

    private static bool IsSupportedCustomEnvironment(LLM_Environment environment) =>
        environment.Id > 0
        && environment.LLM is not (LLM.NotSet or LLM.Shell)
        && CanonicalBaseItems.Any(item => item.Cli == ToPickerCli(environment.LLM));

    private static string ToPickerCli(LLM llm) =>
        LlmParser.ToWireName(llm).ToLowerInvariant();

    private sealed record BasePickerItem(string Cli, string Label);
}
