using VibeRails.Cli;
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
            await LMBootstrap.RunAsync(services);
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
}
