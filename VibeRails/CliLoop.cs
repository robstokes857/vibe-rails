using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VibeRails.Cli;
using VibeRails.DB;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Services.Terminal;
using VibeRails.Utils;

namespace VibeRails;

public static class CliLoop
{
    /// <summary>
    /// Handles all CLI modes. Returns (exit: true) if a CLI command was handled,
    /// or (exit: false) to continue to web server mode.
    /// </summary>
    public static async Task<(bool exit, ParsedArgs parsedArgs)> RunAsync(string[] args, IServiceProvider services)
    {
        ParsedArgs parsedArgs = Configs.ParseArgs(args);

        // Create a scope for resolving scoped services
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;

        // Handle update command
        if (parsedArgs.Command?.ToLowerInvariant() == "update")
        {
            var updateInstaller = scopedServices.GetRequiredService<UpdateInstaller>();
            var lifetime = scopedServices.GetRequiredService<IHostApplicationLifetime>();

            var success = await updateInstaller.InstallUpdateAsync(CancellationToken.None);
            if (success)
            {
                lifetime.StopApplication();
            }
            Environment.ExitCode = success ? 0 : 1;
            return (true, parsedArgs);
        }

        // 0. Handle top-level --help and --version flags (no command required)
        if (parsedArgs.Help)
        {
            CommandRouter.ShowHelp();
            return (true, parsedArgs);
        }

        if (parsedArgs.Version)
        {
            CommandRouter.ShowVersion();
            return (true, parsedArgs);
        }

        // 1. Try new CLI commands first (env, agent, rules, validate, hooks, launch)
        var exitCode = await CommandRouter.RouteAsync(parsedArgs, scopedServices, CancellationToken.None);
        if (exitCode.HasValue)
        {
            Environment.ExitCode = exitCode.Value;
            return (true, parsedArgs);
        }

        // 2. Check for LMBootstrap mode - runs CLI wrapper without web server
        if (parsedArgs.IsLMBootstrap)
        {
            await RunTerminalSessionAsync(parsedArgs, scopedServices);
            return (true, parsedArgs);
        }

        // 3. Check for VCA validation mode - validates rules without web server
        if (parsedArgs.ValidateVca)
        {
            var code = await VcaValidationRunner.RunAsync(services);
            Environment.ExitCode = code;
            return (true, parsedArgs);
        }

        // 4. Check for commit-msg hook validation (called by git commit-msg hook)
        if (!string.IsNullOrEmpty(parsedArgs.CommitMsgFile))
        {
            var code = await VcaValidationRunner.RunCommitMsgValidationAsync(services, parsedArgs.CommitMsgFile);
            Environment.ExitCode = code;
            return (true, parsedArgs);
        }

        // 5. Check for hook management mode
        if (parsedArgs.InstallHook || parsedArgs.UninstallHook)
        {
            var code = await VcaValidationRunner.RunHookManagementAsync(services, parsedArgs.InstallHook);
            Environment.ExitCode = code;
            return (true, parsedArgs);
        }

        // No CLI mode matched - continue to web server
        return (false, parsedArgs);
    }

    private static async Task RunTerminalSessionAsync(ParsedArgs parsedArgs, IServiceProvider services)
    {
        var dbService = services.GetRequiredService<IDbService>();
        var envService = services.GetRequiredService<LlmCliEnvironmentService>();
        var repository = services.GetRequiredService<IRepository>();

        // Resolve LLM type (smart resolution: LLM enum name → base CLI, otherwise → DB lookup)
        LLM llm;
        string? environmentName = null;

        if (Enum.TryParse<LLM>(parsedArgs.LMBootstrapCli, true, out var parsedLlm))
        {
            llm = parsedLlm;
        }
        else
        {
            // Custom environment - resolve via DB
            var env = await repository.FindEnvironmentByNameAsync(parsedArgs.LMBootstrapCli ?? "");
            if (env == null)
                throw new InvalidOperationException($"Unknown CLI or environment: {parsedArgs.LMBootstrapCli}");

            llm = env.LLM;
            environmentName = env.CustomName;
        }

        // Resolve working directory
        var workingDirectory = parsedArgs.WorkDir;
        if (string.IsNullOrEmpty(workingDirectory))
        {
            var gitService = new GitService();
            try
            {
                workingDirectory = await gitService.GetRootPathAsync();
            }
            catch
            {
                workingDirectory = Directory.GetCurrentDirectory();
            }
        }

        // Create runner and run
        var gitServiceForSession = new GitService(workingDirectory);
        var terminalStateService = new TerminalStateService(dbService, gitServiceForSession);
        var runner = new TerminalRunner(terminalStateService, envService);

        var exitCode = await runner.RunCliAsync(llm, workingDirectory, environmentName, parsedArgs.ExtraArgs, CancellationToken.None);
        Environment.ExitCode = exitCode;
    }
}
