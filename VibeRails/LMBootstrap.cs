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
        var cli = args.LMBootstrapCli ?? throw new ArgumentException("CLI name required");
        var envName = args.Env;

        LLM llm = Enum.TryParse<LLM>(cli, true, out var parsedLlm) ? parsedLlm : LLM.NotSet;
        if (llm == LLM.NotSet)
        {
            throw new ArgumentException($"Unsupported CLI/LLM: {cli}");
        }

        // Create a scope for scoped services
        using var scope = services.CreateScope();
        var scopedServices = scope.ServiceProvider;

        var gitService = scopedServices.GetRequiredService<IGitService>();

        string workDir = await gitService.GetRootPathAsync();
        ArgumentNullException.ThrowIfNullOrWhiteSpace(workDir);
        var repo = scopedServices.GetRequiredService<IRepository>();

        // Only persist environment if user provided a name; otherwise use defaults without saving
        LLM_Environment? env = null;
        if (!string.IsNullOrEmpty(envName))
        {
            env = await repo.GetOrCreateEnvironmentAsync(envName, llm);
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

    private static string? GetGitRepoRoot(string startPath)
    {
        var dir = new DirectoryInfo(startPath);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
