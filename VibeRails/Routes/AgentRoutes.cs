using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;

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
        // PUT /api/v1/agents/name - Update a rule file's custom display name
        app.MapPut("/api/v1/agents/name", async (
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

            if (!File.Exists(request.Path))
            {
                return Results.NotFound(new ErrorResponse($"Rule file not found: {request.Path}"));
            }

            await repository.SetAgentCustomNameAsync(request.Path, request.CustomName, cancellationToken);

            return Results.Ok(new UpdateAgentNameResponse(request.Path, request.CustomName));
        }).WithName("UpdateAgentName");

        // GET /api/v1/agents - List all rule files with their rules
        app.MapGet("/api/v1/agents", async (
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
        app.MapGet("/api/v1/agents/rules", async (
            IAgentFileService agentService,
            IRepository repository,
            string path,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
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
        app.MapPost("/api/v1/agents", async (
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

            if (File.Exists(request.Path))
            {
                return Results.BadRequest(new ErrorResponse("Rule file already exists at this path"));
            }

            try
            {
                await agentService.CreateAgentFileAsync(
                    request.Path,
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
            var rules = await agentService.GetRulesWithEnforcementAsync(request.Path, cancellationToken);
            var ruleResponses = rules.Select(r => new RuleWithEnforcementResponse(r.RuleText, r.Enforcement.ToString())).ToList();

            return Results.Ok(new AgentFileResponse(
                Path: request.Path,
                Name: Path.GetFileName(request.Path),
                CustomName: null,
                RuleCount: ruleResponses.Count,
                Rules: ruleResponses
            ));
        }).WithName("CreateAgent");

        // POST /api/v1/agents/rules - Add a rule with enforcement to a rule file
        app.MapPost("/api/v1/agents/rules", async (
            IAgentFileService agentService,
            IGitService gitService,
            IRepository repository,
            AddRuleWithEnforcementRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request.Path) || !File.Exists(request.Path))
            {
                return Results.NotFound(new ErrorResponse($"Rule file not found: {request.Path}"));
            }

            try
            {
                var enforcement = EnforcementParser.Parse(request.Enforcement);

                // Decided before the edit: afterwards our own change is an unstaged difference.
                var stagingIsSafe = await gitService.IsStagingSafeAsync(request.Path, cancellationToken);
                await agentService.AddRuleWithEnforcementAsync(request.Path, request.RuleText, enforcement, cancellationToken);
                await TryStageAgentFileAsync(gitService, request.Path, stagingIsSafe, cancellationToken);

                var updatedRules = await agentService.GetRulesWithEnforcementAsync(request.Path, cancellationToken);
                var ruleResponses = updatedRules.Select(r => new RuleWithEnforcementResponse(r.RuleText, r.Enforcement.ToString())).ToList();
                var customName = await repository.GetAgentCustomNameAsync(request.Path, cancellationToken);
                return Results.Ok(new AgentFileResponse(
                    Path: request.Path,
                    Name: Path.GetFileName(request.Path),
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
        app.MapDelete("/api/v1/agents/rules", async (
            IAgentFileService agentService,
            IGitService gitService,
            IRepository repository,
            AgentRulesRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request.Path) || !File.Exists(request.Path))
            {
                return Results.NotFound(new ErrorResponse($"Rule file not found: {request.Path}"));
            }

            // Decided before the edit: afterwards our own change is an unstaged difference.
            var stagingIsSafe = await gitService.IsStagingSafeAsync(request.Path, cancellationToken);
            await agentService.DeleteRulesAsync(request.Path, cancellationToken, request.Rules);
            await TryStageAgentFileAsync(gitService, request.Path, stagingIsSafe, cancellationToken);

            var updatedRules = await agentService.GetRulesWithEnforcementAsync(request.Path, cancellationToken);
            var ruleResponses = updatedRules.Select(r => new RuleWithEnforcementResponse(r.RuleText, r.Enforcement.ToString())).ToList();
            var customName = await repository.GetAgentCustomNameAsync(request.Path, cancellationToken);
            return Results.Ok(new AgentFileResponse(
                Path: request.Path,
                Name: Path.GetFileName(request.Path),
                CustomName: customName,
                RuleCount: updatedRules.Count,
                Rules: ruleResponses
            ));
        }).WithName("DeleteAgentRules");

        // PUT /api/v1/agents/rules/enforcement - Update enforcement level for a rule
        app.MapPut("/api/v1/agents/rules/enforcement", async (
            IAgentFileService agentService,
            IRepository repository,
            UpdateEnforcementRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request.Path) || !File.Exists(request.Path))
            {
                return Results.NotFound(new ErrorResponse($"Rule file not found: {request.Path}"));
            }

            var enforcement = EnforcementParser.Parse(request.Enforcement);
            await agentService.UpdateRuleEnforcementAsync(request.Path, request.RuleText, enforcement, cancellationToken);

            var updatedRules = await agentService.GetRulesWithEnforcementAsync(request.Path, cancellationToken);
            var ruleResponses = updatedRules.Select(r => new RuleWithEnforcementResponse(r.RuleText, r.Enforcement.ToString())).ToList();
            var customName = await repository.GetAgentCustomNameAsync(request.Path, cancellationToken);
            return Results.Ok(new AgentFileResponse(
                Path: request.Path,
                Name: Path.GetFileName(request.Path),
                CustomName: customName,
                RuleCount: updatedRules.Count,
                Rules: ruleResponses
            ));
        }).WithName("UpdateRuleEnforcement");

        // GET /api/v1/agents/content?path={path} - Get raw rule file content
        app.MapGet("/api/v1/agents/content", async (
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
        // A vc.rules.md covers all files in its directory tree, except files claimed by a deeper vc.rules.md
        app.MapGet("/api/v1/agents/files", async (
            IAgentFileService agentService,
            string path,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(path))
            {
                return Results.BadRequest(new ErrorResponse("Path parameter is required"));
            }

            if (!File.Exists(path))
            {
                return Results.NotFound(new ErrorResponse($"Rule file not found: {path}"));
            }

            try
            {
                var agentDir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (agentDir is null)
                {
                    return Results.BadRequest(new ErrorResponse("Could not determine directory for the given path"));
                }

                // Find all other vc.rules.md files to determine subdirectories that are claimed by deeper rule files
                var allAgentFiles = await agentService.GetAgentFiles(cancellationToken);
                var deeperAgentDirs = allAgentFiles
                    .Select(a => Path.GetDirectoryName(Path.GetFullPath(a)))
                    .Where(d => d is not null
                        && d.Length > agentDir.Length
                        && d.StartsWith(agentDir, StringComparison.OrdinalIgnoreCase))
                    .Cast<string>()
                    .ToList();

                // Enumerate all files in this rule file's directory
                var allFiles = Directory.EnumerateFiles(agentDir, "*.*", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var name = Path.GetFileName(f);
                        // Skip vc.rules.md files themselves
                        if (name.Equals("vc.rules.md", StringComparison.OrdinalIgnoreCase))
                            return false;

                        // Skip files claimed by a deeper agent
                        var fileDir = Path.GetDirectoryName(Path.GetFullPath(f));
                        if (fileDir is null) return false;
                        return !deeperAgentDirs.Any(d =>
                            fileDir.StartsWith(d, StringComparison.OrdinalIgnoreCase));
                    })
                    .Select(f => Path.GetRelativePath(agentDir, f).Replace('\\', '/'))
                    .OrderBy(f => f)
                    .ToList();

                return Results.Ok(new AgentDocumentedFilesResponse(
                    Files: allFiles,
                    TotalCount: allFiles.Count
                ));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to list rule-file scope: {ex.Message}"));
            }
        }).WithName("GetAgentDocumentedFiles");

        // POST /api/v1/agents/validate?path={path} - Run VCA validation for a specific rule file
        app.MapPost("/api/v1/agents/validate", async (
            IRuleValidationService validationService,
            IAgentFileService agentService,
            IGitService gitService,
            string path,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
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
