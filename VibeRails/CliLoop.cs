using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Environments;
using VibeRails.Services.Jobs;
using VibeRails.Services.Terminal;
using VibeRails.Utils;

namespace VibeRails;

public static class CliLoop
{
    /// <summary>
    /// Parses argv and handles the only two argv-handled exits — --help and --version.
    /// Every other invocation falls through to web/LMBootstrap mode in Program.cs.
    /// </summary>
    public static Task<(bool exit, ParsedArgs parsedArgs)> RunAsync(string[] args, IServiceProvider services)
    {
        ParsedArgs parsedArgs = ParserConfigs.ParseArgs(args);

        if (parsedArgs.Help)
        {
            ShowHelp();
            return Task.FromResult((true, parsedArgs));
        }

        if (parsedArgs.Version)
        {
            ShowVersion();
            return Task.FromResult((true, parsedArgs));
        }

        return Task.FromResult((false, parsedArgs));
    }

    /// <summary>
    /// Runs an LLM CLI in the foreground while the web server runs in the background.
    /// Invoked from Program.cs after the host has started when --env was passed.
    ///
    /// Returns the CLI's exit code, and also assigns it to <see cref="Environment.ExitCode"/> for
    /// the plain <c>vb --env</c> path, which has no caller to hand it to.
    /// </summary>
    public static async Task<int> RunTerminalWithWebAsync(
        ParsedArgs parsedArgs,
        IServiceProvider services,
        string? jobRunId = null,
        Action<string>? onSessionCreated = null,
        CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;

        var repository = scopedServices.GetRequiredService<IRepository>();
        var sessionService = scopedServices.GetRequiredService<ITerminalSessionService>();
        var runner = scopedServices.GetRequiredService<TerminalRunner>();
        var llmParser = scopedServices.GetRequiredService<ILlmParser>();

        // Resolve working directory first: an --env-id launch may already have swapped this to a
        // workspace clone, so it is not a safe stand-in for the project an environment belongs to.
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

        // Resolve LLM type (smart resolution: --env-id → exact row, LLM enum name → base CLI,
        // otherwise → DB lookup by name). Use ILlmParser instead of Enum.TryParse so pseudo-CLI
        // strings like "glm-5.2" / "grok-4.6" / "glm-5.3" (which can't be a C# enum name due to
        // punctuation) resolve correctly.
        LLM llm;
        string? environmentName = null;
        LLM_Environment? environment = null;

        var parsedLlm = llmParser.Parse(parsedArgs.LMBootstrapCli);
        if (parsedArgs.EnvId is int envId)
        {
            // The spawning process resolved this row and already checked it against the project,
            // so it is taken as-is. This is also the only branch that can launch an environment
            // whose name happens to match a CLI — EnvironmentRoutes rejects such names now, but
            // rows created before that check exist.
            environment = await repository.GetEnvironmentByIdAsync(envId, cancellationToken)
                ?? throw new InvalidOperationException($"Unknown environment id: {envId}");

            llm = environment.LLM;
            environmentName = environment.CustomName;
        }
        else if (parsedLlm != LLM.NotSet)
        {
            llm = parsedLlm;

            // A pre-existing environment named after a CLI is unreachable by name, and silently
            // launching the base CLI instead would hand the user a session with none of the
            // environment's arguments or credentials. Say so rather than let it look like it worked.
            var shadowed = await repository.FindEnvironmentByNameAsync(parsedArgs.LMBootstrapCli ?? "");
            if (shadowed is not null)
            {
                Log.Warning(
                    "[CLI] Environment {EnvironmentId} is named '{Name}', which is also the built-in CLI " +
                    "'{Llm}' — launching the base CLI. Rename the environment to make it launchable by name.",
                    shadowed.Id, shadowed.CustomName, parsedLlm);
            }
        }
        else
        {
            // Hand-typed `vb --env <name>`. FindEnvironmentByNameAsync is global — names are unique
            // across the table but not scoped to a project — so without this check, typing a name
            // learned from anywhere launches that environment's arguments, permission flags, and
            // per-environment credential directory against whatever repository you are standing in.
            // No workspace swap has happened on this path, so the working directory IS the project.
            environment = await repository.FindEnvironmentByNameAsync(parsedArgs.LMBootstrapCli ?? "");
            if (environment == null)
                throw new InvalidOperationException($"Unknown CLI or environment: {parsedArgs.LMBootstrapCli}");

            if (!ProjectPathComparer.IsVisibleIn(environment.ProjectPath, workingDirectory))
                throw new InvalidOperationException(
                    $"Environment '{environment.CustomName}' belongs to another project and cannot be launched from {workingDirectory}.");

            llm = environment.LLM;
            environmentName = environment.CustomName;
        }

        // This process owns the PTY, so this is where the env's Initial Message is resolved —
        // exactly once per launch. Spawning routes deliberately no longer bake the prompt into
        // extraArgs: {{step:<id>}} references run a shell command here, and resolving where the
        // launch happens keeps the recorded seq-1 input identical to what the CLI receives
        // (RunCliWithWebAsync passes the same string to both). Uses the workingDirectory resolved
        // at the top on purpose: {{git_branch}} and step commands must see the workspace-resolved
        // directory.
        string? initialPrompt = null;
        if (environment is not null && !string.IsNullOrWhiteSpace(environment.CustomPrompt))
        {
            var promptPlaceholders = scopedServices.GetRequiredService<IPromptPlaceholderService>();
            initialPrompt = await promptPlaceholders.ResolveAsync(
                environment.CustomPrompt,
                new PromptPlaceholderContext(workingDirectory, environment.Id, environment.CustomName),
                cancellationToken);
        }

        var exitCode = await runner.RunCliWithWebAsync(
            llm,
            workingDirectory,
            environmentName,
            parsedArgs.ExtraArgs,
            sessionService,
            makeRemote: false,
            cancellationToken,
            initialPrompt: initialPrompt,
            jobRunId: jobRunId,
            onSessionCreated: onSessionCreated,
            renderConsoleOutput: !parsedArgs.IsVsCodeMode);
        Environment.ExitCode = exitCode;
        return exitCode;
    }

    private static void ShowVersion()
    {
        Console.WriteLine($"vb {VersionInfo.Version}");
    }

    private static void ShowHelp()
    {
        Console.WriteLine($"vb {VersionInfo.Version}");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  vb                       Launch the web dashboard (browser opens, server stops on close).");
        Console.WriteLine("  vb --web                 Same as `vb`.");
        Console.WriteLine("  vb --git-guard           Launch the focused staged-change preflight dashboard.");
        Console.WriteLine("  vb --version, -v         Print version and exit.");
        Console.WriteLine("  vb --help,    -h         Print this help and exit.");
        Console.WriteLine();
        Console.WriteLine("Everything else is driven from the dashboard or the VS Code extension.");
    }
}
