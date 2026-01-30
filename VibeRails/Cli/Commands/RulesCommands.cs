using Microsoft.Extensions.DependencyInjection;
using VibeRails.Services;
using VibeRails.Utils;

namespace VibeRails.Cli.Commands
{
    public static class RulesCommands
    {
        public static async Task<int> ExecuteAsync(ParsedArgs args, IServiceProvider services, CancellationToken cancellationToken)
        {
            if (args.Help || string.IsNullOrEmpty(args.SubCommand))
            {
                return ShowHelp();
            }

            var rulesService = services.GetRequiredService<IRulesService>();
            var agentService = services.GetRequiredService<IAgentFileService>();

            return args.SubCommand.ToLowerInvariant() switch
            {
                "list" => ListRules(rulesService, args.Verbose),
                "add" => await AddRuleAsync(args, agentService, rulesService, cancellationToken),
                "help" or "--help" => ShowHelp(),
                _ => ShowUnknownSubcommand(args.SubCommand)
            };
        }

        private static int ShowHelp()
        {
            CliOutput.Help(
                "vb rules <subcommand> [options]",
                "Manage validation rules",
                new Dictionary<string, string>
                {
                    ["list"] = "List all available rules",
                    ["add <agent-path>"] = "Add a rule to an agent file"
                },
                new Dictionary<string, string>
                {
                    ["--verbose"] = "Show rule descriptions",
                    ["--rule <text>"] = "The rule text to add",
                    ["--level <level>"] = "Enforcement level: WARN, COMMIT, or STOP (default: WARN)"
                }
            );

            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  vb rules list");
            Console.WriteLine("  vb rules list --verbose");
            Console.WriteLine("  vb rules add ./AGENTS.md --rule \"Log all file changes\" --level STOP");

            return 0;
        }

        private static int ShowUnknownSubcommand(string subcommand)
        {
            CliOutput.Error($"Unknown subcommand: {subcommand}");
            Console.WriteLine("Run 'vb rules --help' for available subcommands.");
            return 1;
        }

        private static int ListRules(IRulesService rulesService, bool verbose)
        {
            if (verbose)
            {
                var rulesWithDescriptions = rulesService.AllowedRulesWithDescriptions();

                foreach (var rule in rulesWithDescriptions)
                {
                    Console.ForegroundColor = ConsoleColor.White;
                    Console.WriteLine(rule.Name);
                    Console.ResetColor();
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine($"  {rule.Description}");
                    Console.ResetColor();
                    Console.WriteLine();
                }

                Console.WriteLine($"Total: {rulesWithDescriptions.Count} rule(s) available");
            }
            else
            {
                var rules = rulesService.AllowedRules();

                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("Available Rules:");
                Console.ResetColor();
                Console.WriteLine();

                foreach (var rule in rules)
                {
                    Console.WriteLine($"  - {rule}");
                }

                Console.WriteLine();
                Console.WriteLine($"Total: {rules.Count} rule(s)");
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("Use --verbose to see rule descriptions.");
                Console.ResetColor();
            }

            return 0;
        }

        private static async Task<int> AddRuleAsync(ParsedArgs args, IAgentFileService agentService, IRulesService rulesService, CancellationToken cancellationToken)
        {
            var agentPath = args.Target;
            if (string.IsNullOrEmpty(agentPath))
            {
                CliOutput.Error("Agent path is required.");
                Console.WriteLine("Usage: vb rules add <agent-path> --rule \"rule text\" [--level WARN|COMMIT|STOP]");
                return 1;
            }

            if (string.IsNullOrEmpty(args.Rule))
            {
                CliOutput.Error("Rule is required. Use --rule \"rule text\".");
                return 1;
            }

            // Validate the rule exists
            if (!rulesService.TryParse(args.Rule, out _))
            {
                CliOutput.Error($"Invalid rule: {args.Rule}");
                Console.WriteLine();
                Console.WriteLine("Run 'vb rules list' to see available rules.");
                return 1;
            }

            // Make path absolute if relative
            if (!Path.IsPathRooted(agentPath))
            {
                agentPath = Path.GetFullPath(agentPath);
            }

            // Check file exists
            if (!File.Exists(agentPath))
            {
                CliOutput.Error($"Agent file not found: {agentPath}");
                Console.WriteLine("Create it first with 'vb agent create'.");
                return 1;
            }

            // Parse enforcement level
            var enforcement = Enforcement.WARN;
            if (!string.IsNullOrEmpty(args.Level))
            {
                enforcement = EnforcementParser.Parse(args.Level);
            }

            await agentService.AddRuleWithEnforcementAsync(agentPath, args.Rule, enforcement, cancellationToken);

            CliOutput.Success($"Added rule to {Path.GetFileName(agentPath)}:");
            Console.Write($"  {args.Rule} ");
            CliOutput.EnforcementBadge(enforcement.ToString());
            Console.WriteLine();

            return 0;
        }
    }
}
