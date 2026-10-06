using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using RuleFileScope = VibeRails.Services.VCA.RuleFileScope;

namespace VibeRails.Routes;

public static class AgentRoutes
{
    /// <summary>
    /// Stages a rule file after a rule edit, best-effort.
    ///
    /// The edit is already durable on disk by the time this runs, so a staging problem must
    /// never fail the request: a non-Git project, or a rule file outside the repository, is
    /// an ordinary setup rather than a client error, and reporting one as a failure would
    /// leave the caller showing a rule it had in fact already removed.
    ///
    /// Staging is skipped when <paramref name="stagingIsSafe"/> is false — see
    /// <see cref="IGitService.IsStagingSafeAsync"/> for why staging a whole file is only
    /// correct when the index and the working tree already agree.
    /// </summary>
    private static async Task TryStageAgentFileAsync(
        IGitService gitService,
        string path,
        bool stagingIsSafe,
        CancellationToken cancellationToken)
    {
        if (!stagingIsSafe)
        {
            Log.Debug("[Agents] Left {Path} unstaged: staging it would have swept in other changes.", path);
            return;
        }

        try
        {
            await gitService.StageFileAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException)
        {
            Log.Warning(ex, "[Agents] Could not stage {Path} after editing its rules.", path);
        }
    }

    public static void Map(WebApplication app)
    {
        var routes = app.MapGroup("/api/v1/agents").AddEndpointFilter(async (context, next) =>
        {
            try { return await next(context); }
            catch (ArgumentException)
            {
                return Results.BadRequest(new ErrorResponse("Invalid rule file path or rule."));
            }
            catch (UnauthorizedAccessException)
            {
                return Results.BadRequest(new ErrorResponse("Rule file access denied."));
            }
        });
        // PUT /api/v1/agents/name - Update a rule file's custom display name
        routes.MapPut("/name", async (
            IAgentFileService agentService,
            IRepository repository,
            UpdateAgentNameRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request.Path))
            {
                return Results.BadRequest(new ErrorResponse("Path is required"));
            }

            if (string.IsNullOrEmpty(request.CustomName))
            {
                return Results.BadRequest(new ErrorResponse("CustomName is required"));
            }

            var path = await agentService.ResolvePathAsync(request.Path, cancellationToken);
            if (!File.Exists(path))
            {
                return Results.NotFound(new ErrorResponse($"Rule file not found: {request.Path}"));
            }

            await repository.SetAgentCustomNameAsync(path, request.CustomName, cancellationToken);

            return Results.Ok(new UpdateAgentNameResponse(path, request.CustomName));
        }).WithName("UpdateAgentName");

        // GET /api/v1/agents - List all rule files with their rules
        routes.MapGet("", async (
            IAgentFileService agentService,
            IRepository repository,
            CancellationToken cancellationToken) =>
        {
            var agentPaths = await agentService.GetAgentFiles(cancellationToken);

            var agents = new List<AgentFileResponse>();
            foreach (var path in agentPaths)
            {
                var rules = await agentService.GetRulesWithEnforcementAsync(path, cancellationToken);
                var ruleResponses = rules.Select(r => new RuleWithEnforcementResponse(r.RuleText, r.Enforcement.ToString())).ToList();
                var customName = await repository.GetAgentCustomNameAsync(path, cancellationToken);
                agents.Add(new AgentFileResponse(
                    Path: path,
                    Name: Path.GetFileName(path),
                    CustomName: customName,
                    RuleCount: rules.Count,
                    Rules: ruleResponses
                ));
            }

            return Results.Ok(new AgentFileListResponse(agents));
        }).WithName("GetAgents");

        // GET /api/v1/agents/rules?path={path} - Get a specific rule file's rules
        routes.MapGet("/rules", async (
            IAgentFileService agentService,
            IRepository repository,
            string path,
            CancellationToken cancellationToken) =>
        {
            path = await agentService.ResolvePathAsync(path, cancellationToken);
            if (!File.Exists(path))
            {
                return Results.NotFound(new ErrorResponse($"Rule file not found: {path}"));
            }

            var rules = await agentService.GetRulesWithEnforcementAsync(path, cancellationToken);
            var ruleResponses = rules.Select(r => new RuleWithEnforcementResponse(r.RuleText, r.Enforcement.ToString())).ToList();
            var customName = await repository.GetAgentCustomNameAsync(path, cancellationToken);
            return Results.Ok(new AgentFileResponse(
                Path: path,
                Name: Path.GetFileName(path),
                CustomName: customName,
                RuleCount: rules.Count,
                Rules: ruleResponses
            ));
        }).WithName("GetAgentRules");

        // POST /api/v1/agents - Create a new rule file
        routes.MapPost("", async (
            IAgentFileService agentService,
            CreateAgentRequest request,
            CancellationToken cancellationToken) =>
        {
            // Rule files are git-gated for now (listing via GetAgentFiles already is),
            // so block creation when not in a git repo to avoid orphan files the UI
            // then refuses to display.
            if (!Utils.ParserConfigs.GetIsInGit())
            {
                return Results.BadRequest(new ErrorResponse("Rule files require a git repository"));
            }

            if (string.IsNullOrEmpty(request.Path))
            {
                return Results.BadRequest(new ErrorResponse("Path is required"));
            }

            var path = await agentService.ResolvePathAsync(request.Path, cancellationToken);
            if (File.Exists(path))
            {
                return Results.BadRequest(new ErrorResponse("Rule file already exists at this path"));
            }

            try
            {
                await agentService.CreateAgentFileAsync(
                    path,
                    cancellationToken,
                    request.Rules ?? Array.Empty<string>());
            }
            catch (ArgumentException ex)
            {
                // Rule text the writer refuses (malformed path lock, embedded line break) is a bad
                // request, not a server fault. The add-rule route below already answers this way.
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }

            // Fetch the created rules with their enforcement levels
            var rules = await agentService.GetRulesWithEnforcementAsync(path, cancellationToken);
            var ruleResponses = rules.Select(r => new RuleWithEnforcementResponse(r.RuleText, r.Enforcement.ToString())).ToList();

            return Results.Ok(new AgentFileResponse(
                Path: path,
                Name: Path.GetFileName(path),
                CustomName: null,
                RuleCount: ruleResponses.Count,
                Rules: ruleResponses
            ));
        }).WithName("CreateAgent");

        // POST /api/v1/agents/rules - Add a rule with enforcement to a rule file
        routes.MapPost("/rules", async (
            IAgentFileService agentService,
            IGitService gitService,
            IRepository repository,
            AddRuleWithEnforcementRequest request,
            CancellationToken cancellationToken) =>
        {
            var path = await agentService.ResolvePathAsync(request.Path, cancellationToken);
            if (!File.Exists(path))
            {
                return Results.NotFound(new ErrorResponse($"Rule file not found: {request.Path}"));
            }

            try
            {
                var enforcement = EnforcementParser.Parse(request.Enforcement);

                // Decided before the edit: afterwards our own change is an unstaged difference.
                var stagingIsSafe = await gitService.IsStagingSafeAsync(path, cancellationToken);
                await agentService.AddRuleWithEnforcementAsync(path, request.RuleText, enforcement, cancellationToken);
                await TryStageAgentFileAsync(gitService, path, stagingIsSafe, cancellationToken);

                var updatedRules = await agentService.GetRulesWithEnforcementAsync(path, cancellationToken);
                var ruleResponses = updatedRules.Select(r => new RuleWithEnforcementResponse(r.RuleText, r.Enforcement.ToString())).ToList();
                var customName = await repository.GetAgentCustomNameAsync(path, cancellationToken);
                return Results.Ok(new AgentFileResponse(
                    Path: path,
                    Name: Path.GetFileName(path),
                    CustomName: customName,
                    RuleCount: updatedRules.Count,
                    Rules: ruleResponses
                ));
            }
            catch (ArgumentException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).WithName("AddAgentRules");

        // DELETE /api/v1/agents/rules - Delete rules from a rule file
        routes.MapDelete("/rules", async (
            IAgentFileService agentService,
            IGitService gitService,
            IRepository repository,
            AgentRulesRequest request,
            CancellationToken cancellationToken) =>
        {
            var path = await agentService.ResolvePathAsync(request.Path, cancellationToken);
            if (!File.Exists(path))
            {
                return Results.NotFound(new ErrorResponse($"Rule file not found: {request.Path}"));
            }

            // Decided before the edit: afterwards our own change is an unstaged difference.
            var stagingIsSafe = await gitService.IsStagingSafeAsync(path, cancellationToken);
            await agentService.DeleteRulesAsync(path, cancellationToken, request.Rules);
            await TryStageAgentFileAsync(gitService, path, stagingIsSafe, cancellationToken);

            var updatedRules = await agentService.GetRulesWithEnforcementAsync(path, cancellationToken);
            var ruleResponses = updatedRules.Select(r => new RuleWithEnforcementResponse(r.RuleText, r.Enforcement.ToString())).ToList();
            var customName = await repository.GetAgentCustomNameAsync(path, cancellationToken);
            return Results.Ok(new AgentFileResponse(
                Path: path,
                Name: Path.GetFileName(path),
                CustomName: customName,
                RuleCount: updatedRules.Count,
                Rules: ruleResponses
            ));
        }).WithName("DeleteAgentRules");

        // PUT /api/v1/agents/rules/enforcement - Update enforcement level for a rule
        routes.MapPut("/rules/enforcement", async (
            IAgentFileService agentService,
            IRepository repository,
            UpdateEnforcementRequest request,
            CancellationToken cancellationToken) =>
        {
            var path = await agentService.ResolvePathAsync(request.Path, cancellationToken);
            if (!File.Exists(path))
            {
                return Results.NotFound(new ErrorResponse($"Rule file not found: {request.Path}"));
            }

            var enforcement = EnforcementParser.Parse(request.Enforcement);
            await agentService.UpdateRuleEnforcementAsync(path, request.RuleText, enforcement, cancellationToken);

            var updatedRules = await agentService.GetRulesWithEnforcementAsync(path, cancellationToken);
            var ruleResponses = updatedRules.Select(r => new RuleWithEnforcementResponse(r.RuleText, r.Enforcement.ToString())).ToList();
            var customName = await repository.GetAgentCustomNameAsync(path, cancellationToken);
            return Results.Ok(new AgentFileResponse(
                Path: path,
                Name: Path.GetFileName(path),
                CustomName: customName,
                RuleCount: updatedRules.Count,
                Rules: ruleResponses
            ));
        }).WithName("UpdateRuleEnforcement");

        // GET /api/v1/agents/content?path={path} - Get raw rule file content
        routes.MapGet("/content", async (
            IAgentFileService agentService,
            string path,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(path))
            {
                return Results.BadRequest(new ErrorResponse("Path parameter is required"));
            }

            try
            {
                // GetAgentFileContentAsync validates that the path is a real rule file
                var content = await agentService.GetAgentFileContentAsync(path, cancellationToken);
                return Results.Ok(new AgentFileContentResponse(content));
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.BadRequest(new ErrorResponse($"Invalid rule file: {ex.Message}"));
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound(new ErrorResponse($"Rule file not found: {path}"));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to read rule file: {ex.Message}"));
            }
        }).WithName("GetAgentFileContent");

        // GET /api/v1/agents/files?path={path} - Get files on disk that this rule file covers
        // Parent rules also apply beneath nested policies. Omit only this declaring file,
        // Git metadata, and linked paths from the on-disk listing.
        routes.MapGet("/files", async (
            IAgentFileService agentService,
            string path,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(path))
            {
                return Results.BadRequest(new ErrorResponse("Path parameter is required"));
            }

            path = await agentService.ResolvePathAsync(path, cancellationToken);
            if (!File.Exists(path))
            {
                return Results.NotFound(new ErrorResponse($"Rule file not found: {path}"));
            }

            try
            {
                var normalizedPath = Path.GetFullPath(path);
                var comparison = OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal;
                var allAgentFiles = await agentService.GetAgentFiles(cancellationToken);
                if (!allAgentFiles.Any(file => Path.GetFullPath(file).Equals(normalizedPath, comparison)))
                {
                    return Results.BadRequest(new ErrorResponse("Path is not a rule file in this repository."));
                }

                var allFiles = RuleFileScope.ListFiles(normalizedPath, cancellationToken);

                return Results.Ok(new AgentDocumentedFilesResponse(
                    Files: allFiles,
                    TotalCount: allFiles.Count
                ));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to list rule-file scope: {ex.Message}"));
            }
        }).WithName("GetAgentDocumentedFiles");

        // POST /api/v1/agents/validate?path={path} - Run VCA validation for a specific rule file
        routes.MapPost("/validate", async (
            IRuleValidationService validationService,
            IAgentFileService agentService,
            IGitService gitService,
            string path,
            CancellationToken cancellationToken) =>
        {
            path = await agentService.ResolvePathAsync(path, cancellationToken);
            if (!File.Exists(path))
            {
                return Results.NotFound(new ErrorResponse($"Rule file not found: {path}"));
            }

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

            var rules = await agentService.GetRulesWithEnforcementAsync(path, cancellationToken);
            if (rules.Count == 0)
            {
                return Results.Ok(new ValidationResponse(true, "No VCA rules defined in this rule file", new List<ValidationResultResponse>()));
            }

            var rulesWithSource = rules
                .Select(r => new RuleWithSource(r, path))
                .ToList();

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
        }).WithName("ValidateAgentVca");
    }
}
