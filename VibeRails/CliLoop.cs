using Microsoft.Extensions.DependencyInjection;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
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
    /// </summary>
    public static async Task RunTerminalWithWebAsync(ParsedArgs parsedArgs, IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;

        var repository = scopedServices.GetRequiredService<IRepository>();
        var sessionService = scopedServices.GetRequiredService<ITerminalSessionService>();
        var runner = scopedServices.GetRequiredService<TerminalRunner>();
        var llmParser = scopedServices.GetRequiredService<ILlmParser>();

        // Resolve LLM type (smart resolution: LLM enum name → base CLI, otherwise → DB lookup).
        // Use ILlmParser instead of Enum.TryParse so pseudo-CLI strings like "glm-5.2" and
        // "kimi-k3" (which can't be C# enum names due to hyphens/periods) resolve correctly.
        LLM llm;
        string? environmentName = null;
        string? environmentInitialMessage = null;

        var parsedLlm = llmParser.Parse(parsedArgs.LMBootstrapCli);
        if (parsedLlm != LLM.NotSet)
        {
            llm = parsedLlm;
        }
        else
        {
            var env = await repository.FindEnvironmentByNameAsync(parsedArgs.LMBootstrapCli ?? "");
            if (env == null)
                throw new InvalidOperationException($"Unknown CLI or environment: {parsedArgs.LMBootstrapCli}");

            llm = env.LLM;
            environmentName = env.CustomName;
            // Carry the env's initial message into the session as UserInputs sequence=1.
            // The launch command already embeds the prompt in extraArgs (via the spawning
            // route's AppendInitialPrompt), so this value is for DB recording only —
            // CreateSessionAsync passes it to the state service, not to PrepareSession.
            environmentInitialMessage = env.CustomPrompt;
        }

        // Resolve working directory.
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

        var exitCode = await runner.RunCliWithWebAsync(
            llm,
            workingDirectory,
            environmentName,
            parsedArgs.ExtraArgs,
            sessionService,
            makeRemote: false,
            CancellationToken.None,
            environmentInitialMessage);
        Environment.ExitCode = exitCode;
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
