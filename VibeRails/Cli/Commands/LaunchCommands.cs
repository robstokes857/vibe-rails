using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using VibeRails.Services;
using VibeRails.Services.LlmClis;
using VibeRails.Utils;

namespace VibeRails.Cli.Commands
{
    public static class LaunchCommands
    {
        public static async Task<int> ExecuteAsync(ParsedArgs args, IServiceProvider services, CancellationToken cancellationToken)
        {
            if (args.Help || string.IsNullOrEmpty(args.SubCommand))
            {
                return ShowHelp();
            }

            return args.SubCommand.ToLowerInvariant() switch
            {
                "claude" => await LaunchLlmAsync(LLM.Claude, args, services),
                "codex" => await LaunchLlmAsync(LLM.Codex, args, services),
                "gemini" => await LaunchLlmAsync(LLM.Gemini, args, services),
                "vscode" => LaunchVsCode(args),
                "help" or "--help" => ShowHelp(),
                _ => ShowUnknownSubcommand(args.SubCommand)
            };
        }

        private static int ShowHelp()
        {
            CliOutput.Help(
                "vb launch <cli> [options]",
                "Launch LLM CLIs or VS Code",
                new Dictionary<string, string>
                {
                    ["claude"] = "Launch Claude CLI",
                    ["codex"] = "Launch Codex CLI",
                    ["gemini"] = "Launch Gemini CLI",
                    ["vscode"] = "Launch VS Code in current directory"
                },
                new Dictionary<string, string>
                {
                    ["--env <name>"] = "Use a specific environment configuration",
                    ["--dir <path>"] = "Working directory for the CLI",
                    ["-- <args>"] = "Pass additional arguments to the CLI"
                }
            );

            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  vb launch claude");
            Console.WriteLine("  vb launch claude --env research");
            Console.WriteLine("  vb launch codex --dir /path/to/project");
            Console.WriteLine("  vb launch gemini -- --model gemini-pro");
            Console.WriteLine("  vb launch vscode");

            return 0;
        }

        private static int ShowUnknownSubcommand(string subcommand)
        {
            CliOutput.Error($"Unknown CLI: {subcommand}");
            Console.WriteLine("Supported CLIs: claude, codex, gemini, vscode");
            Console.WriteLine("Run 'vb launch --help' for more information.");
            return 1;
        }

        private static async Task<int> LaunchLlmAsync(LLM llm, ParsedArgs args, IServiceProvider services)
        {
            var launchService = services.GetRequiredService<ILaunchLLMService>();

            // Determine working directory
            var workingDirectory = args.WorkDir ?? Directory.GetCurrentDirectory();
            if (!Directory.Exists(workingDirectory))
            {
                CliOutput.Error($"Directory not found: {workingDirectory}");
                return 1;
            }

            var envName = args.Env;
            var extraArgs = args.ExtraArgs ?? Array.Empty<string>();

            CliOutput.Info($"Launching {llm} CLI...");
            if (!string.IsNullOrEmpty(envName))
            {
                Console.WriteLine($"Using environment: {envName}");
            }
            Console.WriteLine($"Working directory: {workingDirectory}");

            var result = launchService.LaunchInTerminal(llm, envName, workingDirectory, extraArgs);

            if (result.Success)
            {
                CliOutput.Success(result.Message);
                return 0;
            }
            else
            {
                CliOutput.Error(result.Message);
                return 1;
            }
        }

        private static int LaunchVsCode(ParsedArgs args)
        {
            var workingDirectory = args.WorkDir ?? Directory.GetCurrentDirectory();
            if (!Directory.Exists(workingDirectory))
            {
                CliOutput.Error($"Directory not found: {workingDirectory}");
                return 1;
            }

            try
            {
                var codeCommand = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                    ? "code.cmd"
                    : "code";

                Process.Start(new ProcessStartInfo
                {
                    FileName = codeCommand,
                    Arguments = $"\"{workingDirectory}\"",
                    UseShellExecute = true,
                    CreateNoWindow = true
                });

                CliOutput.Success($"VS Code launched in {workingDirectory}");
                return 0;
            }
            catch (Exception ex)
            {
                CliOutput.Error($"Failed to launch VS Code: {ex.Message}");
                Console.WriteLine("Make sure VS Code is installed and 'code' is in your PATH.");
                return 1;
            }
        }
    }
}
