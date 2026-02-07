using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;

namespace VibeRails.Routes;

public static class AgentRoutes
{
    public static void Map(WebApplication app)
    {
        // PUT /api/v1/agents/name - Update agent custom name
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
                return Results.NotFound(new ErrorResponse($"Agent file not found: {request.Path}"));
            }

            await repository.SetAgentCustomNameAsync(request.Path, request.CustomName, cancellationToken);

            return Results.Ok(new UpdateAgentNameResponse(request.Path, request.CustomName));
        }).WithName("UpdateAgentName");

        // GET /api/v1/agents - List all agent files with their rules
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

        // GET /api/v1/agents/rules?path={path} - Get a specific agent's rules
        app.MapGet("/api/v1/agents/rules", async (
            IAgentFileService agentService,
            IRepository repository,
            string path,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return Results.NotFound(new ErrorResponse($"Agent file not found: {path}"));
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

        // POST /api/v1/agents - Create new agent file
        app.MapPost("/api/v1/agents", async (
            IAgentFileService agentService,
            CreateAgentRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request.Path))
            {
                return Results.BadRequest(new ErrorResponse("Path is required"));
            }

            if (File.Exists(request.Path))
            {
                return Results.BadRequest(new ErrorResponse("Agent file already exists at this path"));
            }

            await agentService.CreateAgentFileAsync(
                request.Path,
                cancellationToken,
                request.Rules ?? Array.Empty<string>());

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

        // POST /api/v1/agents/rules - Add rule with enforcement to agent file
        app.MapPost("/api/v1/agents/rules", async (
            IAgentFileService agentService,
            IRepository repository,
            AddRuleWithEnforcementRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request.Path) || !File.Exists(request.Path))
            {
                return Results.NotFound(new ErrorResponse($"Agent file not found: {request.Path}"));
            }

            try
            {
                var enforcement = EnforcementParser.Parse(request.Enforcement);
                await agentService.AddRuleWithEnforcementAsync(request.Path, request.RuleText, enforcement, cancellationToken);

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

        // DELETE /api/v1/agents/rules - Delete rules from agent file
        app.MapDelete("/api/v1/agents/rules", async (
            IAgentFileService agentService,
            IRepository repository,
            AgentRulesRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request.Path) || !File.Exists(request.Path))
            {
                return Results.NotFound(new ErrorResponse($"Agent file not found: {request.Path}"));
            }

            await agentService.DeleteRulesAsync(request.Path, cancellationToken, request.Rules);

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
                return Results.NotFound(new ErrorResponse($"Agent file not found: {request.Path}"));
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

        // GET /api/v1/agents/content?path={path} - Get raw agent file content
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
                // GetAgentFileContentAsync validates path is a real agent file
                var content = await agentService.GetAgentFileContentAsync(path, cancellationToken);
                return Results.Ok(new AgentFileContentResponse(content));
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.BadRequest(new ErrorResponse($"Invalid agent file: {ex.Message}"));
            }
            catch (FileNotFoundException)
            {
                return Results.NotFound(new ErrorResponse($"Agent file not found: {path}"));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to read agent file: {ex.Message}"));
            }
        }).WithName("GetAgentFileContent");

        // GET /api/v1/agents/files?path={path} - Get files on disk that this agent covers
        // An agent.md covers all files in its directory tree, except files claimed by a deeper agent.md
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
                return Results.NotFound(new ErrorResponse($"Agent file not found: {path}"));
            }

            try
            {
                var agentDir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (agentDir is null)
                {
                    return Results.BadRequest(new ErrorResponse("Could not determine directory for the given path"));
                }

                // Find all other agent.md files to determine subdirectories that are claimed by deeper agents
                var allAgentFiles = await agentService.GetAgentFiles(cancellationToken);
                var deeperAgentDirs = allAgentFiles
                    .Select(a => Path.GetDirectoryName(Path.GetFullPath(a)))
                    .Where(d => d is not null
                        && d.Length > agentDir.Length
                        && d.StartsWith(agentDir, StringComparison.OrdinalIgnoreCase))
                    .Cast<string>()
                    .ToList();

                // Enumerate all files in this agent's directory
                var allFiles = Directory.EnumerateFiles(agentDir, "*.*", SearchOption.AllDirectories)
                    .Where(f =>
                    {
                        var name = Path.GetFileName(f);
                        // Skip agent.md files themselves
                        if (name.Equals("agent.md", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals("agents.md", StringComparison.OrdinalIgnoreCase))
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
                return Results.BadRequest(new ErrorResponse($"Failed to list agent scope files: {ex.Message}"));
            }
        }).WithName("GetAgentDocumentedFiles");

        // POST /api/v1/agents/validate?path={path} - Run VCA validation for a specific agent
        app.MapPost("/api/v1/agents/validate", async (
            IRuleValidationService validationService,
            IAgentFileService agentService,
            IGitService gitService,
            string path,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return Results.NotFound(new ErrorResponse($"Agent file not found: {path}"));
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
                return Results.Ok(new ValidationResponse(true, "No VCA rules defined in this agent", new List<ValidationResultResponse>()));
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
