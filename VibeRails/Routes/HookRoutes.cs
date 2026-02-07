using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;

namespace VibeRails.Routes;

public static class HookRoutes
{
    public static void Map(WebApplication app)
    {
        // GET /api/v1/hooks/status - Check if pre-commit hook is installed
        app.MapGet("/api/v1/hooks/status", async (
            IHookInstallationService hookService,
            IGitService gitService,
            CancellationToken cancellationToken) =>
        {
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                return Results.Ok(new HookStatusResponse(false, false, "Not in a git repository"));
            }

            var isInstalled = hookService.IsHookInstalled(rootPath);
            return Results.Ok(new HookStatusResponse(true, isInstalled, null));
        }).WithName("GetHookStatus");

        // POST /api/v1/hooks/install - Install the pre-commit hook
        app.MapPost("/api/v1/hooks/install", async (
            IHookInstallationService hookService,
            IGitService gitService,
            CancellationToken cancellationToken) =>
        {
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                return Results.BadRequest(new HookActionResponse(false, "Not in a git repository"));
            }

            var result = await hookService.InstallPreCommitHookAsync(rootPath, cancellationToken);
            var message = result.Success
                ? "Pre-commit hook installed"
                : $"{result.ErrorMessage} {(result.Details != null ? $"({result.Details})" : "")}";
            return Results.Ok(new HookActionResponse(result.Success, message));
        }).WithName("InstallHook");

        // DELETE /api/v1/hooks - Uninstall the pre-commit hook
        app.MapDelete("/api/v1/hooks", async (
            IHookInstallationService hookService,
            IGitService gitService,
            CancellationToken cancellationToken) =>
        {
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                return Results.BadRequest(new HookActionResponse(false, "Not in a git repository"));
            }

            var result = await hookService.UninstallPreCommitHookAsync(rootPath, cancellationToken);
            var message = result.Success
                ? "Pre-commit hook uninstalled"
                : $"{result.ErrorMessage} {(result.Details != null ? $"({result.Details})" : "")}";
            return Results.Ok(new HookActionResponse(result.Success, message));
        }).WithName("UninstallHook");

        // POST /api/v1/hooks/validate - Run VCA validation manually
        app.MapPost("/api/v1/hooks/validate", async (
            IRuleValidationService validationService,
            IAgentFileService agentService,
            IGitService gitService,
            CancellationToken cancellationToken) =>
        {
            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrEmpty(rootPath))
            {
                return Results.BadRequest(new ValidationResponse(false, "Not in a git repository", new List<ValidationResultResponse>()));
            }

            var changedFiles = await gitService.GetChangedFileAsync(cancellationToken);
            if (changedFiles.Count == 0)
            {
                return Results.Ok(new ValidationResponse(true, "No files to validate", new List<ValidationResultResponse>()));
            }

            var agentFiles = await agentService.GetAgentFiles(cancellationToken);
            var rulesWithSource = new List<RuleWithSource>();

            foreach (var agentFile in agentFiles)
            {
                var rules = await agentService.GetRulesWithEnforcementAsync(agentFile, cancellationToken);
                foreach (var rule in rules)
                {
                    rulesWithSource.Add(new RuleWithSource(rule, agentFile));
                }
            }

            if (rulesWithSource.Count == 0)
            {
                return Results.Ok(new ValidationResponse(true, "No VCA rules defined", new List<ValidationResultResponse>()));
            }

            // Use ValidateWithSourceAsync to properly check Files section in each AGENTS.md
            var results = await validationService.ValidateWithSourceAsync(changedFiles, rulesWithSource, rootPath, cancellationToken);

            var hasBlockingViolation = results.Results.Any(r =>
                !r.Passed && (r.Enforcement == Enforcement.COMMIT || r.Enforcement == Enforcement.STOP));

            var resultResponses = results.Results.Select(r => new ValidationResultResponse(
                r.RuleName,
                r.Enforcement.ToString(),
                r.Passed,
                r.Message,
                r.AffectedFiles
            )).ToList();

            return Results.Ok(new ValidationResponse(
                !hasBlockingViolation,
                hasBlockingViolation ? "Validation failed - blocking violations found" : "Validation passed",
                resultResponses
            ));
        }).WithName("ValidateVca");
    }
}
