using ModelContextProtocol.Client;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Services.Mcp;
using VibeRails.Services.Terminal;
namespace VibeRails;

public static class Routes
{
    public static void MapApiEndpoints(this WebApplication app, string launchDirectory)
    {
        MapProjectEndpoints(app, launchDirectory);
        MapEnvironmentEndpoints(app);
        MapCliLaunchEndpoints(app, launchDirectory);
        MapSessionEndpoints(app);
        MapTerminalEndpoints(app, launchDirectory);
        MapMCPEndpoints(app, launchDirectory);
        MapAgentEndpoints(app);
        MapRulesEndpoints(app);
        MapHookEndpoints(app);
        MapGeminiSettingsEndpoints(app);
        MapCodexSettingsEndpoints(app);
        MapClaudeSettingsEndpoints(app);
        MapClaudePlanEndpoints(app);
    }

    private static void MapProjectEndpoints(WebApplication app, string launchDirectory)
    {
        app.MapGet("/api/v1/IsLocal", () =>
        {
            return Results.Ok(new IsLocalResponse(
                IsLocalContext: Utils.Configs.IsLocalContext(),
                LaunchDirectory: launchDirectory,
                RootPath: Utils.Configs.GetRootPath()
            ));
        }).WithName("IsLocal");

        // PUT /api/v1/projects/name - Set custom project name (stored in AgentMetadata table)
        app.MapPut("/api/v1/projects/name", async (
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

            await repository.SetProjectCustomNameAsync(request.Path, request.CustomName, cancellationToken);

            return Results.Ok(new UpdateAgentNameResponse(request.Path, request.CustomName));
        }).WithName("UpdateProjectName");

        // GET /api/v1/projects/name?path={path} - Get custom project name
        app.MapGet("/api/v1/projects/name", async (
            IRepository repository,
            string path,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(path))
            {
                return Results.BadRequest(new ErrorResponse("Path is required"));
            }

            var customName = await repository.GetProjectCustomNameAsync(path, cancellationToken);
            return Results.Ok(new UpdateAgentNameResponse(path, customName ?? ""));
        }).WithName("GetProjectName");
    }

    private static void MapEnvironmentEndpoints(WebApplication app)
    {
        // GET /api/v1/environments - List all custom environments (excludes defaults)
        app.MapGet("/api/v1/environments", async (
            IRepository repository,
            CancellationToken cancellationToken) =>
        {
            var environments = await repository.GetCustomEnvironmentsAsync(cancellationToken);
            var response = environments
                .Select(e => new EnvironmentResponse(
                    e.Id,
                    e.CustomName,
                    e.LLM.ToString(),
                    e.Path,
                    e.CustomArgs,
                    e.CustomPrompt,
                    LLM_Environment.DefaultPrompt,
                    e.LastUsedUTC
                ))
                .ToList();

            return Results.Ok(new EnvironmentListResponse(response));
        }).WithName("GetEnvironments");

        // POST /api/v1/environments - Create new environment
        app.MapPost("/api/v1/environments", async (
            LlmCliEnvironmentService envService,
            IRepository repository,
            CreateEnvironmentRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request.Name))
            {
                return Results.BadRequest(new ErrorResponse("Name is required"));
            }

            if (string.IsNullOrEmpty(request.Cli))
            {
                return Results.BadRequest(new ErrorResponse("CLI type is required"));
            }

            var llm = request.Cli.ToLowerInvariant() switch
            {
                "claude" => LLM.Claude,
                "codex" => LLM.Codex,
                "gemini" => LLM.Gemini,
                _ => LLM.NotSet
            };

            if (llm == LLM.NotSet)
            {
                return Results.BadRequest(new ErrorResponse($"Unknown CLI type: {request.Cli}"));
            }

            var environment = new LLM_Environment
            {
                LLM = llm,
                CustomName = request.Name,
                CustomArgs = request.CustomArgs ?? "",
                CustomPrompt = request.CustomPrompt ?? "",
                CreatedUTC = DateTime.UtcNow,
                LastUsedUTC = DateTime.UtcNow
            };

            await envService.CreateEnvironmentAsync(environment, cancellationToken);
            await repository.SaveEnvironmentAsync(environment, cancellationToken);

            return Results.Ok(new EnvironmentResponse(
                environment.Id,
                environment.CustomName,
                environment.LLM.ToString(),
                environment.Path,
                environment.CustomArgs,
                environment.CustomPrompt,
                LLM_Environment.DefaultPrompt,
                environment.LastUsedUTC
            ));
        }).WithName("CreateEnvironment");

        // PUT /api/v1/environments/{name} - Update environment
        app.MapPut("/api/v1/environments/{name}", async (
            IRepository repository,
            string name,
            UpdateEnvironmentRequest request,
            CancellationToken cancellationToken) =>
        {
            var environments = await repository.GetAllEnvironmentsAsync(cancellationToken);
            var environment = environments.FirstOrDefault(e => e.CustomName == name);

            if (environment == null)
            {
                return Results.NotFound(new ErrorResponse($"Environment not found: {name}"));
            }

            if (request.CustomArgs != null)
            {
                environment.CustomArgs = request.CustomArgs;
            }

            if (request.CustomPrompt != null)
            {
                environment.CustomPrompt = request.CustomPrompt;
            }

            environment.LastUsedUTC = DateTime.UtcNow;
            await repository.UpdateEnvironmentAsync(environment, cancellationToken);

            return Results.Ok(new EnvironmentResponse(
                environment.Id,
                environment.CustomName,
                environment.LLM.ToString(),
                environment.Path,
                environment.CustomArgs,
                environment.CustomPrompt,
                LLM_Environment.DefaultPrompt,
                environment.LastUsedUTC
            ));
        }).WithName("UpdateEnvironment");

        // DELETE /api/v1/environments/{name} - Delete environment
        app.MapDelete("/api/v1/environments/{name}", async (
            IRepository repository,
            string name,
            CancellationToken cancellationToken) =>
        {
            var environments = await repository.GetAllEnvironmentsAsync(cancellationToken);
            var environment = environments.FirstOrDefault(e => e.CustomName == name);

            if (environment == null)
            {
                return Results.NotFound(new ErrorResponse($"Environment not found: {name}"));
            }

            // Prevent deletion of default environments
            if (environment.CustomName == "Default")
            {
                return Results.BadRequest(new ErrorResponse("Cannot delete default environments"));
            }

            await repository.DeleteEnvironmentAsync(environment.Id, cancellationToken);
            return Results.Ok(new OK("Environment deleted"));
        }).WithName("DeleteEnvironment");
    }

    private static void MapCliLaunchEndpoints(WebApplication app, string launchDirectory)
    {
        app.MapGet("/api/v1/environments/{name}/launch", (LlmCliEnvironmentService envService, string name, LLM llm) =>
        {
            var envVars = envService.GetEnvironmentVariables(name, llm);
            return Results.Ok(envVars);
        }).WithName("GetLaunchEnvironment");

        app.MapPost("/api/v1/cli/launch/{cli}", async (
            ILaunchLLMService launchService,
            IRepository repository,
            string cli,
            LaunchCliRequest? request,
            CancellationToken cancellationToken) =>
        {
            var llm = cli.ToLowerInvariant() switch
            {
                "claude" => LLM.Claude,
                "codex" => LLM.Codex,
                "gemini" => LLM.Gemini,
                _ => LLM.NotSet
            };

            if (llm == LLM.NotSet)
            {
                return Results.BadRequest(new ErrorResponse($"Unknown CLI type: {cli}"));
            }

            var workingDirectory = request?.WorkingDirectory ?? launchDirectory;
            var args = request?.Args?.ToList() ?? new List<string>();
            var envName = request?.EnvironmentName;

            // If using a custom environment, look up its custom args and update last used
            if (!string.IsNullOrEmpty(envName))
            {
                var environment = await repository.GetEnvironmentByNameAndLlmAsync(envName, llm, cancellationToken);

                if (environment != null)
                {
                    if (!string.IsNullOrEmpty(environment.CustomArgs))
                    {
                        // Parse custom args and prepend them
                        var customArgs = environment.CustomArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        args.InsertRange(0, customArgs);
                    }

                    // Update last used timestamp
                    environment.LastUsedUTC = DateTime.UtcNow;
                    await repository.UpdateEnvironmentAsync(environment, cancellationToken);
                }
            }

            var result = launchService.LaunchInTerminal(llm, envName, workingDirectory, args.ToArray());

            return Results.Ok(new LaunchCliResponse(
                Success: result.Success,
                ExitCode: 0,
                Message: result.Message,
                StandardOutput: "",
                StandardError: result.Success ? "" : result.Message
            ));
        }).WithName("LaunchCli");

        app.MapPost("/api/cli/launch/vscode", () =>
        {
            try
            {
                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "code",
                    Arguments = ".",
                    WorkingDirectory = launchDirectory,
                    UseShellExecute = true
                });

                if (process == null)
                {
                    return Results.BadRequest(new ErrorResponse("Failed to start VS Code. Make sure 'code' command is in your PATH."));
                }

                return Results.Ok(new LaunchCliResponse(
                    Success: true,
                    ExitCode: 0,
                    Message: $"VS Code launched in {launchDirectory}",
                    StandardOutput: "",
                    StandardError: ""
                ));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to launch VS Code: {ex.Message}"));
            }
        }).WithName("LaunchVSCode");
    }

    private static void MapSessionEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/sessions/{sessionId}/logs", async (
            IDbService dbService,
            string sessionId,
            CancellationToken cancellationToken) =>
        {
            var result = await dbService.GetSessionWithLogsAsync(sessionId, cancellationToken);
            if (result == null)
            {
                return Results.NotFound(new ErrorResponse($"Session not found: {sessionId}"));
            }
            return Results.Ok(result);
        }).WithName("GetSessionLogs");

        app.MapGet("/api/v1/sessions/recent", async (
            IDbService dbService,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var sessions = await dbService.GetRecentSessionsAsync(limit ?? 10, cancellationToken);
            return Results.Ok(sessions);
        }).WithName("GetRecentSessions");

    }

    private static void MapTerminalEndpoints(WebApplication app, string launchDirectory)
    {
        // GET /api/v1/terminal/status - Check if terminal session is active
        app.MapGet("/api/v1/terminal/status", (ITerminalSessionService terminalService) =>
        {
            return Results.Ok(new TerminalStatusResponse(terminalService.HasActiveSession));
        }).WithName("GetTerminalStatus");

        // POST /api/v1/terminal/start - Start a new terminal session
        app.MapPost("/api/v1/terminal/start", async (
            ITerminalSessionService terminalService,
            StartTerminalRequest? request) =>
        {
            if (terminalService.HasActiveSession)
            {
                return Results.BadRequest(new ErrorResponse("A terminal session is already active. Stop it first."));
            }

            var workDir = request?.WorkingDirectory ?? launchDirectory;
            var success = await terminalService.StartSessionAsync(workDir);

            if (!success)
            {
                return Results.BadRequest(new ErrorResponse("Failed to start terminal session"));
            }

            return Results.Ok(new TerminalStatusResponse(true));
        }).WithName("StartTerminal");

        // POST /api/v1/terminal/stop - Stop the current terminal session
        app.MapPost("/api/v1/terminal/stop", async (ITerminalSessionService terminalService) =>
        {
            if (!terminalService.HasActiveSession)
            {
                return Results.Ok(new TerminalStatusResponse(false));
            }

            await terminalService.StopSessionAsync();
            return Results.Ok(new TerminalStatusResponse(false));
        }).WithName("StopTerminal");

        // WebSocket endpoint for terminal I/O
        app.Map("/api/v1/terminal/ws", async (HttpContext context, ITerminalSessionService terminalService) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            if (!terminalService.HasActiveSession)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("No active terminal session. Start one first via POST /api/v1/terminal/start");
                return;
            }

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            await terminalService.HandleWebSocketAsync(webSocket, context.RequestAborted);
        });
    }

    private static void MapMCPEndpoints(WebApplication app, string launchDirectory)
    {

        // MCP Endpoints
        app.MapGet("/api/v1/mcp/status", (McpSettings settings) =>
        {
            var available = !string.IsNullOrEmpty(settings.ServerPath) && File.Exists(settings.ServerPath);
            var message = available
                ? "MCP server executable found"
                : "MCP server executable not found. Build MCP_Server project first.";
            return Results.Ok(new McpStatusResponse(available, settings.ServerPath, message));
        }).WithName("GetMcpStatus");

        app.MapGet("/api/v1/mcp/tools", async (McpSettings settings, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(settings.ServerPath) || !File.Exists(settings.ServerPath))
            {
                return Results.BadRequest(new ErrorResponse("MCP server executable not found. Build MCP_Server project first."));
            }

            try
            {
                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = settings.ServerPath
                });

                await using var client = await McpClientService.ConnectAsync(transport, cancellationToken: cancellationToken);
                var tools = await client.GetAvailableToolsAsync(cancellationToken);
                var toolInfos = tools.Select(t => new McpToolInfo(t.Name, t.Description ?? "")).ToList();
                return Results.Ok(toolInfos);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to connect to MCP server: {ex.Message}"));
            }
        }).WithName("GetMcpTools");
        app.MapPost("/api/v1/mcp/tools/{name}", async (
    McpSettings settings,
    string name,
    McpToolCallRequest request,
    CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(settings.ServerPath) || !File.Exists(settings.ServerPath))
            {
                return Results.BadRequest(new McpToolCallResponse(false, "", "MCP server executable not found."));
            }

            try
            {
                var transport = new StdioClientTransport(new StdioClientTransportOptions
                {
                    Command = settings.ServerPath
                });

                await using var client = await McpClientService.ConnectAsync(transport, cancellationToken: cancellationToken);
                var result = await client.CallToolAsync(name, request.Arguments, cancellationToken);
                return Results.Ok(new McpToolCallResponse(true, result));
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new McpToolCallResponse(false, "", ex.Message));
            }
        }).WithName("CallMcpTool");
    }

    private static void MapAgentEndpoints(WebApplication app)
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

        // DELETE /api/v1/agents?path={path} - Delete agent file
        app.MapDelete("/api/v1/agents", async (
            IAgentFileService agentService,
            string path,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return Results.NotFound(new ErrorResponse($"Agent file not found: {path}"));
            }

            await agentService.DeleteAgentFileAsync(path, cancellationToken);
            return Results.Ok(new OK("Agent file deleted"));
        }).WithName("DeleteAgent");

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

    private static void MapRulesEndpoints(WebApplication app)
    {
        app.MapGet("/api/v1/rules", (IRulesService rulesService) =>
        {
            var rules = rulesService.AllowedRules();
            return Results.Ok(new AvailableRulesResponse(rules));
        }).WithName("GetAvailableRules");

        // GET /api/v1/rules/details - Get available rules with descriptions
        app.MapGet("/api/v1/rules/details", (IRulesService rulesService) =>
        {
            var rulesWithDescriptions = rulesService.AllowedRulesWithDescriptions()
                .Select(r => new RuleWithDescription(r.Name, r.Description))
                .ToList();
            return Results.Ok(new AvailableRulesWithDescriptionsResponse(rulesWithDescriptions));
        }).WithName("GetAvailableRulesWithDescriptions");
    }

    private static void MapHookEndpoints(WebApplication app)
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

            var success = await hookService.InstallPreCommitHookAsync(rootPath, cancellationToken);
            return Results.Ok(new HookActionResponse(success, success ? "Pre-commit hook installed" : "Failed to install hook"));
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

            var success = await hookService.UninstallPreCommitHookAsync(rootPath, cancellationToken);
            return Results.Ok(new HookActionResponse(success, success ? "Pre-commit hook uninstalled" : "Failed to uninstall hook"));
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

    private static void MapGeminiSettingsEndpoints(WebApplication app)
    {
        // GET /api/v1/gemini/settings/{envName} - Get Gemini settings for an environment
        app.MapGet("/api/v1/gemini/settings/{envName}", async (
            IGeminiLlmCliEnvironment geminiEnv,
            string envName,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var settings = await geminiEnv.GetSettings(envName, cancellationToken);
                return Results.Ok(settings);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to read Gemini settings: {ex.Message}"));
            }
        }).WithName("GetGeminiSettings");

        // PUT /api/v1/gemini/settings/{envName} - Update Gemini settings for an environment
        app.MapPut("/api/v1/gemini/settings/{envName}", async (
            IGeminiLlmCliEnvironment geminiEnv,
            string envName,
            GeminiSettingsDto settings,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await geminiEnv.SaveSettings(envName, settings, cancellationToken);
                return Results.Ok(settings);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to save Gemini settings: {ex.Message}"));
            }
        }).WithName("UpdateGeminiSettings");
    }

    private static void MapCodexSettingsEndpoints(WebApplication app)
    {
        // GET /api/v1/codex/settings/{envName} - Get Codex settings for an environment
        app.MapGet("/api/v1/codex/settings/{envName}", async (
            ICodexLlmCliEnvironment codexEnv,
            string envName,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var settings = await codexEnv.GetSettings(envName, cancellationToken);
                return Results.Ok(settings);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to read Codex settings: {ex.Message}"));
            }
        }).WithName("GetCodexSettings");

        // PUT /api/v1/codex/settings/{envName} - Update Codex settings for an environment
        app.MapPut("/api/v1/codex/settings/{envName}", async (
            ICodexLlmCliEnvironment codexEnv,
            string envName,
            CodexSettingsDto settings,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await codexEnv.SaveSettings(envName, settings, cancellationToken);
                return Results.Ok(settings);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to save Codex settings: {ex.Message}"));
            }
        }).WithName("UpdateCodexSettings");
    }

    private static void MapClaudeSettingsEndpoints(WebApplication app)
    {
        // GET /api/v1/claude/settings/{envName} - Get Claude settings for an environment
        app.MapGet("/api/v1/claude/settings/{envName}", async (
            IClaudeLlmCliEnvironment claudeEnv,
            string envName,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var settings = await claudeEnv.GetSettings(envName, cancellationToken);
                return Results.Ok(settings);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to read Claude settings: {ex.Message}"));
            }
        }).WithName("GetClaudeSettings");

        // PUT /api/v1/claude/settings/{envName} - Update Claude settings for an environment
        app.MapPut("/api/v1/claude/settings/{envName}", async (
            IClaudeLlmCliEnvironment claudeEnv,
            string envName,
            ClaudeSettingsDto settings,
            CancellationToken cancellationToken) =>
        {
            try
            {
                await claudeEnv.SaveSettings(envName, settings, cancellationToken);
                return Results.Ok(settings);
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new ErrorResponse($"Failed to save Claude settings: {ex.Message}"));
            }
        }).WithName("UpdateClaudeSettings");
    }

    private static void MapClaudePlanEndpoints(WebApplication app)
    {
        // GET /api/v1/plans/session/{sessionId} - Get plans for a session
        app.MapGet("/api/v1/plans/session/{sessionId}", async (
            IDbService dbService,
            string sessionId,
            CancellationToken cancellationToken) =>
        {
            var plans = await dbService.GetClaudePlansForSessionAsync(sessionId, cancellationToken);
            return Results.Ok(new ClaudePlanListResponse(plans, plans.Count));
        }).WithName("GetSessionPlans");

        // GET /api/v1/plans/recent - Get recent plans across all sessions
        app.MapGet("/api/v1/plans/recent", async (
            IDbService dbService,
            int? limit,
            CancellationToken cancellationToken) =>
        {
            var plans = await dbService.GetRecentClaudePlansAsync(limit ?? 20, cancellationToken);
            return Results.Ok(new ClaudePlanListResponse(plans, plans.Count));
        }).WithName("GetRecentPlans");

        // GET /api/v1/plans/{planId} - Get a single plan
        app.MapGet("/api/v1/plans/{planId}", async (
            IDbService dbService,
            long planId,
            CancellationToken cancellationToken) =>
        {
            var plan = await dbService.GetClaudePlanAsync(planId, cancellationToken);
            if (plan == null)
            {
                return Results.NotFound(new ErrorResponse($"Plan not found: {planId}"));
            }
            return Results.Ok(plan);
        }).WithName("GetPlan");

        // POST /api/v1/plans - Create a new plan
        app.MapPost("/api/v1/plans", async (
            IDbService dbService,
            CreateClaudePlanRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request.SessionId))
            {
                return Results.BadRequest(new ErrorResponse("SessionId is required"));
            }

            if (string.IsNullOrEmpty(request.PlanContent))
            {
                return Results.BadRequest(new ErrorResponse("PlanContent is required"));
            }

            var planId = await dbService.CreateClaudePlanAsync(
                request.SessionId,
                request.UserInputId,
                request.PlanFilePath,
                request.PlanContent,
                request.PlanSummary);

            var plan = await dbService.GetClaudePlanAsync(planId, cancellationToken);
            return Results.Ok(plan);
        }).WithName("CreatePlan");

        // PUT /api/v1/plans/{planId}/status - Update plan status
        app.MapPut("/api/v1/plans/{planId}/status", async (
            IDbService dbService,
            long planId,
            UpdateClaudePlanStatusRequest request,
            CancellationToken cancellationToken) =>
        {
            var plan = await dbService.GetClaudePlanAsync(planId, cancellationToken);
            if (plan == null)
            {
                return Results.NotFound(new ErrorResponse($"Plan not found: {planId}"));
            }

            if (string.IsNullOrEmpty(request.Status))
            {
                return Results.BadRequest(new ErrorResponse("Status is required"));
            }

            await dbService.UpdateClaudePlanStatusAsync(planId, request.Status);
            return Results.Ok(new OK("Status updated"));
        }).WithName("UpdatePlanStatus");

        // POST /api/v1/plans/{planId}/complete - Mark plan as completed
        app.MapPost("/api/v1/plans/{planId}/complete", async (
            IDbService dbService,
            long planId,
            CancellationToken cancellationToken) =>
        {
            var plan = await dbService.GetClaudePlanAsync(planId, cancellationToken);
            if (plan == null)
            {
                return Results.NotFound(new ErrorResponse($"Plan not found: {planId}"));
            }

            await dbService.CompleteClaudePlanAsync(planId);
            var updatedPlan = await dbService.GetClaudePlanAsync(planId, cancellationToken);
            return Results.Ok(updatedPlan);
        }).WithName("CompletePlan");
    }
}

