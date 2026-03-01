using Microsoft.Extensions.DependencyInjection;
using VibeRails.Services;
using VibeRails.Utils;

namespace VibeRails.Cli.Commands
{
    public static class HooksCommands
    {
        public static async Task<int> ExecuteAsync(ParsedArgs args, IServiceProvider services, CancellationToken cancellationToken)
        {
            if (args.Help || string.IsNullOrEmpty(args.SubCommand))
            {
                return ShowHelp();
            }

            var hookService = services.GetRequiredService<IHookInstallationService>();
            var gitService = services.GetRequiredService<IGitService>();

            return args.SubCommand.ToLowerInvariant() switch
            {
                "status" => await StatusAsync(hookService, gitService, cancellationToken),
                "help" or "--help" => ShowHelp(),
                _ => ShowUnknownSubcommand(args.SubCommand)
            };
        }

        private static int ShowHelp()
        {
            CliOutput.Help(
                "vb hooks <subcommand>",
                "Check git hooks status (hooks are auto-installed)",
                new Dictionary<string, string>
                {
                    ["status"] = "Check if VCA hooks are installed"
                },
                null
            );

            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  vb hooks status");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("Note: Git hooks are automatically installed when Vibe Rails runs.");
            Console.ResetColor();

            return 0;
        }

        private static int ShowUnknownSubcommand(string subcommand)
        {
            CliOutput.Error($"Unknown subcommand: {subcommand}");
            Console.WriteLine("Run 'vb hooks --help' for available subcommands.");
            return 1;
        }

        private static async Task<int> StatusAsync(IHookInstallationService hookService, IGitService gitService, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(ParserConfigs.GetRootPath()))
            {
                CliOutput.Error("Not in a git repository.");
                return 1;
            }

            var rootPath = await gitService.GetRootPathAsync(cancellationToken);
            var isInstalled = hookService.IsHookInstalled(rootPath);

            CliOutput.Detail("Git Repository", "Yes");

            if (isInstalled)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                CliOutput.Detail("Pre-commit Hook", "Installed");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                CliOutput.Detail("Pre-commit Hook", "Not Installed");
                Console.ResetColor();
                Console.WriteLine();
                CliOutput.Info("Hooks will be installed automatically when you start Vibe Rails.");
            }

            return 0;
        }
    }
}
