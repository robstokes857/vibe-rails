using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;

namespace VibeRails.Services.Jobs;

/// <summary>Read-only discovery. Approval is compared with saved hashes, never granted by browsing.</summary>
public sealed class AutomationScriptCatalogService(IBoardFileIndexService files, IAutomationScriptService scripts, IJobStore jobs)
{
    private const long MaxCatalogBytes = 32 * 1024 * 1024;

    /// <summary>Validate bounded repository candidates using the same rules as save/run.</summary>
    public async Task<AutomationScriptCatalogResponse> ReadAsync(string project, CancellationToken ct, string? query = null)
    {
        if ((query?.Trim().Length ?? 0) > BoardFileIndexService.MaxQueryLength)
            throw JobServiceException.BadRequest($"Search must be {BoardFileIndexService.MaxQueryLength} characters or fewer.");
        // Filter the cached path index before applying either the candidate or content budget.
        var candidates = await files.GetScriptPathsAsync(project, ct, query);
        var definitions = await jobs.GetJobsAsync(project, cancellationToken: ct);
        var approvals = definitions.SelectMany(job => job.Actions ?? [])
            .Where(action => action.Kind == JobActionKind.Script && action.ApprovedHash is not null).ToList();
        var result = new List<AutomationScriptCatalogEntry>();
        long bytes = 0;
        foreach (var path in candidates.Files)
        {
            ct.ThrowIfCancellationRequested();
            var runtime = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".py" => JobScriptRuntime.Python, ".ps1" => JobScriptRuntime.PowerShell, _ => JobScriptRuntime.Bash
            };
            try
            {
                // ScriptExists checks containment, links and per-file size before inspecting bytes.
                if (!scripts.ScriptExists(project, path))
                    throw new AutomationScriptValidationException("Missing or unsafe script path");
                bytes += new FileInfo(Path.Combine(project, path)).Length;
                if (bytes > MaxCatalogBytes)
                {
                    return new(result, true);
                }
                var normalized = await scripts.NormalizeAsync(project, new(null, JobActionKind.Script, ScriptPath: path, ScriptRuntime: runtime), ct);
                var approved = approvals.Any(action => string.Equals(action.ScriptPath, normalized.ScriptPath, StringComparison.Ordinal)
                    && action.ScriptRuntime == runtime && string.Equals(action.ApprovedHash, normalized.ApprovedHash, StringComparison.OrdinalIgnoreCase));
                result.Add(new(path, runtime, approved, null));
            }
            catch (Exception error) when (error is AutomationScriptValidationException or IOException or UnauthorizedAccessException)
            {
                result.Add(new(path, runtime, false, error is AutomationScriptValidationException ? error.Message : "Script could not be read"));
            }
        }
        return new(result, candidates.Truncated);
    }
}
