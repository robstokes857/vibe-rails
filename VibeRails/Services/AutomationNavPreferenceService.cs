using System.Text.Json;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Jobs;
using VibeRails.Services.PythonScripts;
using VibeRails.Utils;

namespace VibeRails.Services;

public interface IAutomationNavPreferenceService
{
    Task<AutomationNavPreferencesResponse> GetAsync(CancellationToken cancellationToken = default);
    Task<AutomationNavPreferencesResponse> SaveAsync(
        UpdateAutomationNavPreferencesRequest request,
        CancellationToken cancellationToken = default);
    Task<AutomationNavPreferencesResponse> ResetAsync(CancellationToken cancellationToken = default);
}

public sealed class AutomationNavPreferenceValidationException(string message) : Exception(message);

/// <summary>
/// Order and visibility for the nav-bar Automation launcher, mirroring how the LLM
/// picker preferences treat Custom Environments. The catalog is the current project's
/// automations (keyed job:{id}) followed by every Python script in the global scripts
/// folder (keyed script:{name}, each carrying its signing status); preferences live
/// entirely in one GlobalCache document. Saves and resets only ever touch keys that are
/// in the current catalog, so another project's stored choices survive untouched.
/// </summary>
public sealed class AutomationNavPreferenceService(
    IRepository repository,
    IJobService jobService,
    IPythonScriptService pythonScriptService) : IAutomationNavPreferenceService
{
    public const string CacheKey = "ui.automation-nav.v1";
    public const string AutomationKind = "automation";
    public const string ScriptKind = "script";
    internal const int DocumentVersion = 1;
    private const int MaxItems = 1000;

    private readonly IRepository _repository = repository;
    private readonly IJobService _jobService = jobService;
    private readonly IPythonScriptService _pythonScriptService = pythonScriptService;

    public async Task<AutomationNavPreferencesResponse> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var catalog = await BuildCatalogAsync(cancellationToken);
        var document = await ReadDocumentAsync(cancellationToken);
        return Resolve(catalog, document);
    }

    public async Task<AutomationNavPreferencesResponse> SaveAsync(
        UpdateAutomationNavPreferencesRequest request,
        CancellationToken cancellationToken = default)
    {
        var catalog = await BuildCatalogAsync(cancellationToken);
        var document = await ReadDocumentAsync(cancellationToken);
        var currentCatalog = Resolve(catalog, document: null).Items;
        var normalized = ValidateAndNormalize(request.Items, currentCatalog);

        var currentKeys = normalized.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        var mergedOrder = normalized
            .OrderBy(item => item.Order)
            .Select(item => item.Key)
            .Concat((document?.Order ?? []).Where(key => !currentKeys.Contains(key)))
            .ToList();
        var mergedHidden = normalized
            .Where(item => !item.Enabled)
            .Select(item => item.Key)
            .Concat((document?.HiddenKeys ?? []).Where(key => !currentKeys.Contains(key)))
            .ToList();

        await WriteDocumentAsync(
            new AutomationNavPreferenceDocument(DocumentVersion, mergedOrder, mergedHidden),
            cancellationToken);

        return new AutomationNavPreferencesResponse(normalized);
    }

    public async Task<AutomationNavPreferencesResponse> ResetAsync(
        CancellationToken cancellationToken = default)
    {
        var catalog = await BuildCatalogAsync(cancellationToken);
        var document = await ReadDocumentAsync(cancellationToken);

        if (document != null)
        {
            var currentKeys = catalog.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            await WriteDocumentAsync(
                new AutomationNavPreferenceDocument(
                    DocumentVersion,
                    document.Order.Where(key => !currentKeys.Contains(key)).ToList(),
                    document.HiddenKeys.Where(key => !currentKeys.Contains(key)).ToList()),
                cancellationToken);
        }

        return Resolve(catalog, document: null);
    }

    /// <summary>
    /// The launcher catalog: the current project's automations (jobs) followed by every
    /// Python script, signed or not, each with its signing status so the launcher can
    /// disable the unsigned ones. Scripts are global (they live in the home dir), so
    /// they appear whatever project is open.
    /// </summary>
    private async Task<List<AutomationNavPreferenceItem>> BuildCatalogAsync(
        CancellationToken cancellationToken)
    {
        var catalog = (await GetScopedJobsAsync(cancellationToken))
            .Select(job => new AutomationNavPreferenceItem(
                ToKey(job),
                AutomationKind,
                job.Name,
                job.Id,
                Enabled: true,
                Order: 0))
            .ToList();

        PythonScriptListResponse scripts;
        try
        {
            scripts = await _pythonScriptService.GetStatusAsync(cancellationToken);
        }
        catch (PythonScriptValidationException ex)
        {
            // The signing file is transiently locked by another process. The launcher
            // must still render its automations; scripts simply reappear on the next read.
            Log.Warning(ex, "[AutomationNav] Python script catalog unavailable; listing automations only.");
            scripts = new PythonScriptListResponse(PinConfigured: false, ScriptsDirectory: string.Empty, Scripts: []);
        }

        catalog.AddRange(scripts.Scripts.Select(script => new AutomationNavPreferenceItem(
            ScriptKey(script.Name),
            ScriptKind,
            script.Name,
            JobId: 0,
            Enabled: true,
            Order: 0,
            Status: script.Status)));
        return catalog;
    }

    private static AutomationNavPreferencesResponse Resolve(
        IReadOnlyCollection<AutomationNavPreferenceItem> fullCatalog,
        AutomationNavPreferenceDocument? document)
    {
        var hiddenKeys = document?.Version == DocumentVersion
            ? document.HiddenKeys.ToHashSet(StringComparer.Ordinal)
            : [];
        var catalog = fullCatalog
            .Select(item => item with { Enabled = !hiddenKeys.Contains(item.Key) })
            .ToList();

        var savedOrder = document?.Version == DocumentVersion ? document.Order : null;
        if (savedOrder is { Count: > 0 })
        {
            var byKey = catalog.ToDictionary(item => item.Key, StringComparer.Ordinal);
            var ordered = new List<AutomationNavPreferenceItem>(catalog.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var key in savedOrder)
            {
                if (seen.Add(key) && byKey.TryGetValue(key, out var item))
                {
                    ordered.Add(item);
                }
            }

            ordered.AddRange(catalog.Where(item => seen.Add(item.Key)));
            catalog = ordered;
        }

        return new AutomationNavPreferencesResponse(catalog
            .Select((item, index) => item with { Order = index })
            .ToList());
    }

    private static List<AutomationNavPreferenceItem> ValidateAndNormalize(
        List<AutomationNavPreferenceItem>? submitted,
        IReadOnlyCollection<AutomationNavPreferenceItem> currentCatalog)
    {
        if (submitted == null)
        {
            throw new AutomationNavPreferenceValidationException("Items are required.");
        }

        if (submitted.Count > MaxItems)
        {
            throw new AutomationNavPreferenceValidationException(
                $"A maximum of {MaxItems} launcher items can be saved.");
        }

        var expectedByKey = currentCatalog.ToDictionary(item => item.Key, StringComparer.Ordinal);
        if (submitted.Count != expectedByKey.Count)
        {
            throw new AutomationNavPreferenceValidationException(
                "The launcher snapshot must contain every current automation exactly once.");
        }

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenPositions = new HashSet<int>();
        foreach (var item in submitted)
        {
            if (string.IsNullOrWhiteSpace(item.Key) || !seenKeys.Add(item.Key))
            {
                throw new AutomationNavPreferenceValidationException(
                    $"Duplicate or malformed launcher key: '{item.Key}'.");
            }

            if (!expectedByKey.TryGetValue(item.Key, out var expected))
            {
                throw new AutomationNavPreferenceValidationException(
                    $"Unknown launcher item: '{item.Key}'.");
            }

            if (!string.Equals(item.Label, expected.Label, StringComparison.Ordinal)
                || !string.Equals(item.Kind, expected.Kind, StringComparison.Ordinal)
                || item.JobId != expected.JobId)
            {
                throw new AutomationNavPreferenceValidationException(
                    $"Launcher item '{item.Key}' does not match the current automation catalog.");
            }

            if (item.Order < 0 || !seenPositions.Add(item.Order))
            {
                throw new AutomationNavPreferenceValidationException(
                    "The launcher snapshot contains a duplicate or invalid position.");
            }
        }

        return submitted
            .OrderBy(item => item.Order)
            .Select((item, index) => item with
            {
                Order = index,
                Status = expectedByKey[item.Key].Status
            })
            .ToList();
    }

    private async Task<AutomationNavPreferenceDocument?> ReadDocumentAsync(
        CancellationToken cancellationToken)
    {
        var json = await _repository.GetGlobalCacheValueAsync(CacheKey, cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize(
                json,
                AppJsonSerializerContext.Default.AutomationNavPreferenceDocument);
            if (document?.Version != DocumentVersion
                || document.Order == null
                || document.HiddenKeys == null
                || document.Order.Any(string.IsNullOrWhiteSpace)
                || document.HiddenKeys.Any(string.IsNullOrWhiteSpace))
            {
                return null;
            }

            return document;
        }
        catch (JsonException)
        {
            // A corrupt/legacy cache value must never prevent the nav launcher from
            // rendering. The next successful save replaces it with a valid document.
            return null;
        }
    }

    private Task WriteDocumentAsync(
        AutomationNavPreferenceDocument document,
        CancellationToken cancellationToken) =>
        _repository.SetGlobalCacheValueAsync(
            CacheKey,
            JsonSerializer.Serialize(
                document,
                AppJsonSerializerContext.Default.AutomationNavPreferenceDocument),
            cancellationToken);

    /// <summary>
    /// The automations the nav launcher may show: the current project's, exactly as the
    /// Automation page lists them. Outside a project there is no catalog at all, so a
    /// save or reset made there can never rewrite a project's stored choices.
    /// </summary>
    private async Task<List<JobResponse>> GetScopedJobsAsync(CancellationToken cancellationToken)
    {
        var projectPath = ParserConfigs.GetRootPath();
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return [];
        }

        var response = await _jobService.GetJobsAsync(projectPath, cancellationToken);
        return response.Jobs;
    }

    private static string ToKey(JobResponse job) => $"job:{job.Id}";

    // Keyed by the script's real on-disk name (case-sensitive), matching how
    // PythonScriptService stores approvals. Lowercasing here would collapse two distinct
    // scripts on a case-sensitive filesystem (Backup.py vs backup.py) into one key and
    // throw when the catalog is built into a dictionary.
    private static string ScriptKey(string name) => $"script:{name}";
}
