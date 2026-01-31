using VibeRails.Cli.Commands;
using VibeRails.Utils;

namespace VibeRails.Cli
{
    /// <summary>
    /// Routes CLI commands to their handlers
    /// </summary>
    public static class CommandRouter
    {
        /// <summary>
        /// Routes the command and returns exit code (0 = success, non-zero = error)
        /// Returns null if no command was handled (continue to web server)
        /// </summary>
        public static async Task<int?> RouteAsync(ParsedArgs args, IServiceProvider services, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(args.Command))
            {
                return null; // No command, continue to web server
            }

            return args.Command.ToLowerInvariant() switch
            {
                "env" => await EnvCommands.ExecuteAsync(args, services, cancellationToken),
                "agent" => await AgentCommands.ExecuteAsync(args, services, cancellationToken),
                "rules" => await RulesCommands.ExecuteAsync(args, services, cancellationToken),
                "validate" => await ValidateCommands.ExecuteAsync(args, services, cancellationToken),
                "hooks" => await HooksCommands.ExecuteAsync(args, services, cancellationToken),
                "launch" => await LaunchCommands.ExecuteAsync(args, services, cancellationToken),
                "gemini" => await GeminiCommands.ExecuteAsync(args, services, cancellationToken),
                "codex" => await CodexCommands.ExecuteAsync(args, services, cancellationToken),
                "claude" => await ClaudeCommands.ExecuteAsync(args, services, cancellationToken),
                "help" or "--help" or "-h" => ShowHelpAndReturn(),
                _ => ShowUnknownCommand(args.Command)
            };
        }

        private static int ShowHelpAndReturn()
        {
            ShowHelp();
            return 0;
        }

        public static void ShowHelp()
        {
            CliOutput.Help(
                "vb [command] [subcommand] [options]",
                "Vibe Rails - LLM CLI Environment Manager and VCA Validation Tool",
                new Dictionary<string, string>
                {
                    ["env"] = "Manage LLM CLI environments",
                    ["agent"] = "Manage AGENTS.md files",
                    ["rules"] = "List available validation rules",
                    ["validate"] = "Run VCA validation",
                    ["hooks"] = "Check git hooks status",
                    ["launch"] = "Launch LLM CLIs (claude, codex, gemini, vscode)",
                    ["gemini"] = "Configure Gemini CLI settings for environments",
                    ["codex"] = "Configure Codex CLI settings for environments",
                    ["claude"] = "Configure Claude CLI settings for environments"
                },
                new Dictionary<string, string>
                {
                    ["--help, -h"] = "Show this help message",
                    ["--version, -v"] = "Show version information"
                }
            );

            Console.WriteLine();
            Console.WriteLine("Run 'vb [command] --help' for more information on a command.");
            Console.WriteLine();
            Console.WriteLine("When run without arguments, launches the web UI.");
        }

        public static void ShowVersion()
        {
            Console.WriteLine($"vb {VersionInfo.Version}");
        }

        private static int ShowUnknownCommand(string command)
        {
            CliOutput.Error($"Unknown command: {command}");
            Console.WriteLine();
            Console.WriteLine("Run 'vb --help' for a list of available commands.");
            return 1;
        }
    }
}
