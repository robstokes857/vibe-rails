using Microsoft.Extensions.DependencyInjection;
using VibeRails.Services;
using VibeRails.Utils;

namespace VibeRails.Cli.Commands
{
    public static class ValidateCommands
    {
        public static async Task<int> ExecuteAsync(ParsedArgs args, IServiceProvider services, CancellationToken cancellationToken)
        {
            if (args.Help)
            {
                return ShowHelp();
            }

            // If no subcommand, default to running validation
            if (string.IsNullOrEmpty(args.SubCommand) || args.SubCommand.ToLowerInvariant() == "run")
            {
                return await RunValidationAsync(args, services);
            }

            return args.SubCommand.ToLowerInvariant() switch
            {
                "help" or "--help" => ShowHelp(),
                _ => ShowUnknownSubcommand(args.SubCommand)
            };
        }

        private static int ShowHelp()
        {
            CliOutput.Help(
                "vb validate [options]",
                "Run VCA validation on changed files",
                new Dictionary<string, string>
                {
                    ["run"] = "Run validation (default)"
                },
                new Dictionary<string, string>
                {
                    ["--staged"] = "Only validate staged files (for pre-commit)",
                    ["--agent <path>"] = "Validate against a specific agent file only"
                }
            );

            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  vb validate");
            Console.WriteLine("  vb validate --staged");

            return 0;
        }

        private static int ShowUnknownSubcommand(string subcommand)
        {
            CliOutput.Error($"Unknown subcommand: {subcommand}");
            Console.WriteLine("Run 'vb validate --help' for usage.");
            return 1;
        }

        private static async Task<int> RunValidationAsync(ParsedArgs args, IServiceProvider services)
        {
            // Delegate to the existing VcaValidationRunner
            // Set the appropriate flags based on args
            if (args.Staged)
            {
                args.PreCommit = true;
            }
            args.ValidateVca = true;

            return await VcaValidationRunner.RunAsync(services);
        }
    }
}
