using Microsoft.Extensions.DependencyInjection;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Utils;

namespace VibeRails.Cli.Commands
{
    public static class EnvCommands
    {
        public static async Task<int> ExecuteAsync(ParsedArgs args, IServiceProvider services, CancellationToken cancellationToken)
        {
            if (args.Help || string.IsNullOrEmpty(args.SubCommand))
            {
                return ShowHelp();
            }

            var repository = services.GetRequiredService<IRepository>();

            return args.SubCommand.ToLowerInvariant() switch
            {
                "list" => await ListAsync(repository, cancellationToken),
                "create" => await CreateAsync(args, repository, cancellationToken),
                "update" => await UpdateAsync(args, repository, cancellationToken),
                "delete" => await DeleteAsync(args, repository, cancellationToken),
                "show" => await ShowAsync(args, repository, cancellationToken),
                "help" or "--help" => ShowHelp(),
                _ => ShowUnknownSubcommand(args.SubCommand)
            };
        }

        private static int ShowHelp()
        {
            CliOutput.Help(
                "vb env <subcommand> [options]",
                "Manage LLM CLI environments",
                new Dictionary<string, string>
                {
                    ["list"] = "List all environments",
                    ["create <name>"] = "Create a new environment",
                    ["update <name>"] = "Update an existing environment",
                    ["delete <name>"] = "Delete an environment",
                    ["show <name>"] = "Show environment details"
                },
                new Dictionary<string, string>
                {
                    ["--cli <type>"] = "CLI type: claude, codex, or gemini (required for create)",
                    ["--args <args>"] = "Custom CLI arguments",
                    ["--prompt <text>"] = "Custom system prompt"
                }
            );

            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  vb env list");
            Console.WriteLine("  vb env create research --cli claude --args \"--model opus\"");
            Console.WriteLine("  vb env update research --prompt \"Focus on code review\"");
            Console.WriteLine("  vb env delete research");

            return 0;
        }

        private static int ShowUnknownSubcommand(string subcommand)
        {
            CliOutput.Error($"Unknown subcommand: {subcommand}");
            Console.WriteLine("Run 'vb env --help' for available subcommands.");
            return 1;
        }

        private static async Task<int> ListAsync(IRepository repository, CancellationToken cancellationToken)
        {
            var environments = await repository.GetCustomEnvironmentsAsync(cancellationToken);

            if (environments.Count == 0)
            {
                CliOutput.Info("No custom environments found. Create one with 'vb env create'.");
                return 0;
            }

            var headers = new[] { "NAME", "CLI", "ARGS", "LAST USED" };
            var rows = environments.Select(e => new[]
            {
                e.CustomName,
                e.LLM.ToString().ToLowerInvariant(),
                string.IsNullOrEmpty(e.CustomArgs) ? "-" : TruncateString(e.CustomArgs, 30),
                e.LastUsedUTC.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
            }).ToList();

            CliOutput.Table(headers, rows);
            return 0;
        }

        private static async Task<int> CreateAsync(ParsedArgs args, IRepository repository, CancellationToken cancellationToken)
        {
            var name = args.Target;
            if (string.IsNullOrEmpty(name))
            {
                CliOutput.Error("Environment name is required.");
                Console.WriteLine("Usage: vb env create <name> --cli <claude|codex|gemini>");
                return 1;
            }

            if (string.IsNullOrEmpty(args.Cli))
            {
                CliOutput.Error("CLI type is required. Use --cli claude, --cli codex, or --cli gemini.");
                return 1;
            }

            if (!TryParseLlm(args.Cli, out var llm))
            {
                CliOutput.Error($"Invalid CLI type: {args.Cli}. Must be claude, codex, or gemini.");
                return 1;
            }

            // Check if environment already exists
            var existing = await repository.GetEnvironmentByNameAndLlmAsync(name, llm, cancellationToken);
            if (existing != null)
            {
                CliOutput.Error($"Environment '{name}' already exists for {llm}.");
                Console.WriteLine("Use 'vb env update' to modify an existing environment.");
                return 1;
            }

            var environment = new LLM_Environment
            {
                CustomName = name,
                LLM = llm,
                Path = "",
                CustomArgs = args.Args ?? "",
                CustomPrompt = args.Prompt ?? "",
                CreatedUTC = DateTime.UtcNow,
                LastUsedUTC = DateTime.UtcNow
            };

            await repository.SaveEnvironmentAsync(environment, cancellationToken);

            CliOutput.Success($"Environment '{name}' created for {llm}.");
            return 0;
        }

        private static async Task<int> UpdateAsync(ParsedArgs args, IRepository repository, CancellationToken cancellationToken)
        {
            var name = args.Target;
            if (string.IsNullOrEmpty(name))
            {
                CliOutput.Error("Environment name is required.");
                Console.WriteLine("Usage: vb env update <name> [--args <args>] [--prompt <text>]");
                return 1;
            }

            // Find environment - we need to check all LLM types since name might match multiple
            LLM_Environment? environment = null;
            foreach (var llm in new[] { LLM.Claude, LLM.Codex, LLM.Gemini })
            {
                environment = await repository.GetEnvironmentByNameAndLlmAsync(name, llm, cancellationToken);
                if (environment != null) break;
            }

            if (environment == null)
            {
                CliOutput.Error($"Environment '{name}' not found.");
                Console.WriteLine("Run 'vb env list' to see available environments.");
                return 1;
            }

            // Update fields if provided
            if (args.Args != null)
            {
                environment.CustomArgs = args.Args;
            }
            if (args.Prompt != null)
            {
                environment.CustomPrompt = args.Prompt;
            }

            environment.LastUsedUTC = DateTime.UtcNow;
            await repository.UpdateEnvironmentAsync(environment, cancellationToken);

            CliOutput.Success($"Environment '{name}' updated.");
            return 0;
        }

        private static async Task<int> DeleteAsync(ParsedArgs args, IRepository repository, CancellationToken cancellationToken)
        {
            var name = args.Target;
            if (string.IsNullOrEmpty(name))
            {
                CliOutput.Error("Environment name is required.");
                Console.WriteLine("Usage: vb env delete <name>");
                return 1;
            }

            if (name.Equals("Default", StringComparison.OrdinalIgnoreCase))
            {
                CliOutput.Error("Cannot delete the Default environment.");
                return 1;
            }

            // Find environment
            LLM_Environment? environment = null;
            foreach (var llm in new[] { LLM.Claude, LLM.Codex, LLM.Gemini })
            {
                environment = await repository.GetEnvironmentByNameAndLlmAsync(name, llm, cancellationToken);
                if (environment != null) break;
            }

            if (environment == null)
            {
                CliOutput.Error($"Environment '{name}' not found.");
                return 1;
            }

            await repository.DeleteEnvironmentAsync(environment.Id, cancellationToken);

            CliOutput.Success($"Environment '{name}' deleted.");
            return 0;
        }

        private static async Task<int> ShowAsync(ParsedArgs args, IRepository repository, CancellationToken cancellationToken)
        {
            var name = args.Target;
            if (string.IsNullOrEmpty(name))
            {
                CliOutput.Error("Environment name is required.");
                Console.WriteLine("Usage: vb env show <name>");
                return 1;
            }

            // Find environment
            LLM_Environment? environment = null;
            foreach (var llm in new[] { LLM.Claude, LLM.Codex, LLM.Gemini })
            {
                environment = await repository.GetEnvironmentByNameAndLlmAsync(name, llm, cancellationToken);
                if (environment != null) break;
            }

            if (environment == null)
            {
                CliOutput.Error($"Environment '{name}' not found.");
                return 1;
            }

            CliOutput.Detail("Name", environment.CustomName);
            CliOutput.Detail("CLI", environment.LLM.ToString().ToLowerInvariant());
            CliOutput.Detail("Custom Args", string.IsNullOrEmpty(environment.CustomArgs) ? "(none)" : environment.CustomArgs);
            CliOutput.Detail("Custom Prompt", string.IsNullOrEmpty(environment.CustomPrompt) ? "(none)" : environment.CustomPrompt);
            CliOutput.Detail("Created", environment.CreatedUTC.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
            CliOutput.Detail("Last Used", environment.LastUsedUTC.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

            if (!string.IsNullOrEmpty(environment.CustomPrompt))
            {
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("Prompt:");
                Console.ResetColor();
                Console.WriteLine(environment.CustomPrompt);
            }

            return 0;
        }

        private static bool TryParseLlm(string value, out LLM llm)
        {
            llm = value.ToLowerInvariant() switch
            {
                "claude" => LLM.Claude,
                "codex" => LLM.Codex,
                "gemini" => LLM.Gemini,
                _ => LLM.NotSet
            };
            return llm != LLM.NotSet;
        }

        private static string TruncateString(string value, int maxLength)
        {
            if (value.Length <= maxLength) return value;
            return value.Substring(0, maxLength - 3) + "...";
        }
    }
}
