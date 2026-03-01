using Microsoft.Extensions.DependencyInjection;
using VibeRails.DB;
using VibeRails.Services;
using VibeRails.Utils;

namespace VibeRails.Cli.Commands
{
    public static class AgentCommands
    {
        public static async Task<int> ExecuteAsync(ParsedArgs args, IServiceProvider services, CancellationToken cancellationToken)
        {
            if (args.Help || string.IsNullOrEmpty(args.SubCommand))
            {
                return ShowHelp();
            }

            var agentService = services.GetRequiredService<IAgentFileService>();
            var repository = services.GetRequiredService<IRepository>();

            return args.SubCommand.ToLowerInvariant() switch
            {
                "list" => await ListAsync(agentService, repository, cancellationToken),
                "create" => await CreateAsync(args, agentService, cancellationToken),
                "help" or "--help" => ShowHelp(),
                _ => ShowUnknownSubcommand(args.SubCommand)
            };
        }

        private static int ShowHelp()
        {
            CliOutput.Help(
                "vb agent <subcommand> [options]",
                "Manage AGENTS.md files",
                new Dictionary<string, string>
                {
                    ["list"] = "List all agent.md files in the project",
                    ["create <path>"] = "Create a new AGENTS.md file"
                },
                new Dictionary<string, string>
                {
                    ["--rules <list>"] = "Comma-separated list of rules to include"
                }
            );

            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  vb agent list");
            Console.WriteLine("  vb agent create ./src/AGENTS.md --rules \"Log all file changes,Package file changes\"");

            return 0;
        }

        private static int ShowUnknownSubcommand(string subcommand)
        {
            CliOutput.Error($"Unknown subcommand: {subcommand}");
            Console.WriteLine("Run 'vb agent --help' for available subcommands.");
            return 1;
        }

        private static async Task<int> ListAsync(IAgentFileService agentService, IRepository repository, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(ParserConfigs.GetRootPath()))
            {
                CliOutput.Error("Not in a local project context. Run from within a git repository.");
                return 1;
            }

            var agentFiles = await agentService.GetAgentFiles(cancellationToken);

            if (agentFiles.Count == 0)
            {
                CliOutput.Info("No agent files found. Create one with 'vb agent create'.");
                return 0;
            }

            var headers = new[] { "PATH", "RULES", "CUSTOM NAME" };
            var rows = new List<string[]>();

            foreach (var path in agentFiles)
            {
                var rules = await agentService.GetRulesWithEnforcementAsync(path, cancellationToken);
                var customName = await repository.GetAgentCustomNameAsync(path, cancellationToken);

                // Make path relative to root
                var rootPath = ParserConfigs.GetRootPath();
                var relativePath = path;
                if (!string.IsNullOrEmpty(rootPath) && path.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    relativePath = path.Substring(rootPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }

                rows.Add(new[]
                {
                    relativePath,
                    rules.Count.ToString(),
                    customName ?? "-"
                });
            }

            CliOutput.Table(headers, rows);

            // Show rules breakdown if verbose
            Console.WriteLine();
            Console.WriteLine($"Total: {agentFiles.Count} agent file(s)");

            return 0;
        }

        private static async Task<int> CreateAsync(ParsedArgs args, IAgentFileService agentService, CancellationToken cancellationToken)
        {
            var path = args.Target;
            if (string.IsNullOrEmpty(path))
            {
                CliOutput.Error("Path is required.");
                Console.WriteLine("Usage: vb agent create <path> [--rules \"rule1,rule2\"]");
                return 1;
            }

            // Validate the path ends with agent.md or agents.md
            var fileName = Path.GetFileName(path);
            if (!fileName.Equals("agent.md", StringComparison.OrdinalIgnoreCase) &&
                !fileName.Equals("agents.md", StringComparison.OrdinalIgnoreCase))
            {
                CliOutput.Error("Agent file must be named 'agent.md' or 'agents.md'.");
                return 1;
            }

            // Make path absolute if relative
            if (!Path.IsPathRooted(path))
            {
                path = Path.GetFullPath(path);
            }

            // Check if file already exists
            if (File.Exists(path))
            {
                CliOutput.Error($"File already exists: {path}");
                Console.WriteLine("Use 'vb rules add' to add rules to an existing agent file.");
                return 1;
            }

            // Ensure directory exists
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Parse rules if provided
            var rules = Array.Empty<string>();
            if (!string.IsNullOrEmpty(args.Rules))
            {
                rules = args.Rules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            await agentService.CreateAgentFileAsync(path, cancellationToken, rules);

            CliOutput.Success($"Created agent file: {path}");
            if (rules.Length > 0)
            {
                Console.WriteLine($"Added {rules.Length} rule(s).");
            }

            return 0;
        }
    }
}
