using Microsoft.Extensions.DependencyInjection;
using Pty.Net;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Utils;

namespace VibeRails;

public static class LMBootstrap
{
    public static async Task RunAsync(IServiceProvider services)
    {
        var args = Configs.GetAarguments();
        var envInput = args.LMBootstrapCli ?? throw new ArgumentException("CLI or environment name required");

        // Create a scope for scoped services
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;

        var gitService = scopedServices.GetRequiredService<IGitService>();
        var repo = scopedServices.GetRequiredService<IRepository>();

        // Smart resolution: is it an LLM name (claude/codex/gemini) or a custom environment name?
        LLM llm;
        string? envName = null;

        if (Enum.TryParse<LLM>(envInput, true, out var parsedLlm) && parsedLlm != LLM.NotSet)
        {
            // It's a base CLI name (claude, codex, gemini)
            llm = parsedLlm;
        }
        else
        {
            // It's a custom environment name — look it up in the DB
            var env = await repo.FindEnvironmentByNameAsync(envInput);
            if (env == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: '{envInput}' is not a recognized CLI or environment name.");
                Console.ResetColor();
                Console.WriteLine("Valid CLIs: claude, codex, gemini");
                Console.WriteLine("Or create a custom environment: vb env create <name> --cli <claude|codex|gemini>");
                return;
            }
            llm = env.LLM;
            envName = env.CustomName;
        }

        var cli = llm.ToString().ToLowerInvariant();

        // --workdir is optional: use it if provided, otherwise detect from git repo
        string? workDir = !string.IsNullOrEmpty(args.WorkDir)
            ? args.WorkDir
            : await gitService.GetRootPathAsync();

        if (string.IsNullOrWhiteSpace(workDir))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: No working directory found.");
            Console.ResetColor();
            Console.WriteLine("Either run from inside a git repository or pass --workdir <path>.");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  vb --env claude --workdir /path/to/project");
            Console.WriteLine("  cd /path/to/project && vb --env claude");
            return;
        }

        // Only persist environment if using a custom environment name
        LLM_Environment? envRecord = null;
        if (!string.IsNullOrEmpty(envName))
        {
            envRecord = await repo.GetOrCreateEnvironmentAsync(envName, llm);
        }

        var extraArgs = args.ExtraArgs.ToList();

        // DISABLED: Custom prompts are currently disabled
        // Get the effective prompt (custom or default)
        // var prompt = (env != null && !string.IsNullOrWhiteSpace(env.CustomPrompt))
        //     ? env.CustomPrompt
        //     : LLM_Environment.DefaultPrompt;

        // Add prompt using CLI-specific flags that maintain interactive mode
        // if (!string.IsNullOrWhiteSpace(prompt))
        // {
        //     switch (llm)
        //     {
        //         case LLM.Gemini:
        //             // Gemini: -i/--prompt-interactive executes prompt and continues in interactive mode
        //             extraArgs.Add("-i");
        //             extraArgs.Add(prompt);
        //             break;
        //         case LLM.Claude:
        //             // Claude: -p executes prompt and continues interactively
        //             extraArgs.Add("-p");
        //             extraArgs.Add(prompt);
        //             break;
        //         case LLM.Codex:
        //             // Codex: check if it supports similar flags, skip for now if not
        //             break;
        //     }
        // }

        // Initialize session logging
        var sessionId = Guid.NewGuid().ToString();
        var dbService = CreateDbService();
        await dbService.CreateSessionAsync(sessionId, cli, envName, workDir);

        // Record initial git state as sequence 0 (baseline for tracking changes)
        var initialCommitHash = await gitService.GetCurrentCommitHashAsync();
        await dbService.InsertUserInputAsync(
            sessionId,
            sequence: 0,
            inputText: "[SESSION_START]",
            gitCommitHash: initialCommitHash
        );

        int exitCode = 0;
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        // Create input accumulator to track user inputs
        var inputAccumulator = new InputAccumulator(async inputText =>
        {
            await dbService.RecordUserInputAsync(sessionId, inputText, gitService, cts.Token);
        });

        try
        {
            using var terminal = new EzTerminal(
                userOutputHandler: input =>
                {
                    // Track user input for analytics
                    inputAccumulator.Append(input);
                },
                terminalOutputHandler: output =>
                {
                    Console.Write(output);

                    // Filter transient content (spinners, progress bars) before logging
                    if (!TerminalOutputFilter.IsTransient(output))
                    {
                        _ = dbService.LogSessionOutputAsync(sessionId, output, false);
                    }
                },
                cancellationToken: cts.Token);

            terminal.WithWorkingDirectory(workDir);

            // Set environment from launcher config if envName provided
            if (!string.IsNullOrEmpty(envName))
            {
                var envBasePath = Configs.GetEnvPath();
                var cliLower = cli.ToLowerInvariant();

                // Set the appropriate environment variables based on CLI type
                switch (cliLower)
                {
                    case "claude":
                        var claudeConfigPath = Path.Combine(envBasePath, envName, "claude");
                        terminal.WithEnvironment("CLAUDE_CONFIG_DIR", claudeConfigPath);
                        break;

                    case "codex":
                        var codexConfigPath = Path.Combine(envBasePath, envName, "codex");
                        terminal.WithEnvironment("CODEX_HOME", codexConfigPath);
                        break;

                    case "gemini":
                        // Gemini uses XDG Base Directory Specification
                        var geminiBasePath = Path.Combine(envBasePath, envName, "gemini");
                        terminal.WithEnvironment("XDG_CONFIG_HOME", Path.Combine(geminiBasePath, "config"));
                        terminal.WithEnvironment("XDG_DATA_HOME", Path.Combine(geminiBasePath, "data"));
                        terminal.WithEnvironment("XDG_CACHE_HOME", Path.Combine(geminiBasePath, "cache"));
                        terminal.WithEnvironment("XDG_STATE_HOME", Path.Combine(geminiBasePath, "state"));
                        break;

                    default:
                        var defaultConfigPath = Path.Combine(envBasePath, envName, cliLower);
                        terminal.WithEnvironment($"{cli.ToUpperInvariant()}_CONFIG_DIR", defaultConfigPath);
                        break;
                }
            }

            var command = extraArgs.Count > 0 ? $"{cli} {string.Join(" ", extraArgs.Select(a => a.Contains(' ') ? $"\"{a}\"" : a))}" : cli;
            await terminal.Run(command, cts.Token);
        }
        catch (OperationCanceledException)
        {
            exitCode = -1;
        }
        catch (Exception ex)
        {
            exitCode = 1;
            await dbService.LogSessionOutputAsync(sessionId, $"Error: {ex.Message}", true);
        }
        finally
        {
            await dbService.CompleteSessionAsync(sessionId, exitCode);
            Console.WriteLine();
            Console.WriteLine("Session ended.");
        }
    }

    private static IDbService CreateDbService()
    {
        // State path is already set by Init.StartUpChecks -> FileService.InitGlobalSave
        var dbService = new DbService();
        dbService.InitializeDatabase();
        return dbService;
    }
}
