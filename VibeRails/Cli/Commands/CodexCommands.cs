using Microsoft.Extensions.DependencyInjection;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Cli.Commands
{
    public static class CodexCommands
    {
        private static readonly string[] ValidSandboxValues = { "read-only", "workspace-write", "danger-full-access" };
        private static readonly string[] ValidApprovalValues = { "untrusted", "on-failure", "on-request", "never" };

        public static async Task<int> ExecuteAsync(ParsedArgs args, IServiceProvider services, CancellationToken cancellationToken)
        {
            if (args.Help || string.IsNullOrEmpty(args.SubCommand))
            {
                return ShowHelp();
            }

            var codexEnv = services.GetRequiredService<ICodexLlmCliEnvironment>();

            return args.SubCommand.ToLowerInvariant() switch
            {
                "settings" => await SettingsAsync(args, codexEnv, cancellationToken),
                "get" => await GetAsync(args, codexEnv, cancellationToken),
                "set" => await SetAsync(args, codexEnv, cancellationToken),
                "help" or "--help" => ShowHelp(),
                _ => ShowUnknownSubcommand(args.SubCommand)
            };
        }

        private static int ShowHelp()
        {
            CliOutput.Help(
                "vb codex <subcommand> <env-name> [options]",
                "Configure Codex CLI settings for environments",
                new Dictionary<string, string>
                {
                    ["settings <env>"] = "Show all Codex settings for an environment",
                    ["get <env> <setting>"] = "Get a specific setting value",
                    ["set <env>"] = "Update Codex settings for an environment"
                },
                new Dictionary<string, string>
                {
                    ["--model <value>"] = "Model to use (e.g., o3, gpt-5-codex)",
                    ["--sandbox <value>"] = "Sandbox policy: read-only, workspace-write, danger-full-access",
                    ["--approval <value>"] = "Approval mode: untrusted, on-failure, on-request, never",
                    ["--full-auto"] = "Enable full-auto mode (approval=on-request + sandbox=workspace-write)",
                    ["--no-full-auto"] = "Disable full-auto mode",
                    ["--search"] = "Enable web search capabilities",
                    ["--no-search"] = "Disable web search capabilities"
                }
            );

            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  vb codex settings myenv");
            Console.WriteLine("  vb codex get myenv model");
            Console.WriteLine("  vb codex set myenv --model o3 --sandbox workspace-write");
            Console.WriteLine("  vb codex set myenv --full-auto --search");

            return 0;
        }

        private static int ShowUnknownSubcommand(string subcommand)
        {
            CliOutput.Error($"Unknown subcommand: {subcommand}");
            Console.WriteLine("Run 'vb codex --help' for available subcommands.");
            return 1;
        }

        private static async Task<int> SettingsAsync(ParsedArgs args, ICodexLlmCliEnvironment codexEnv, CancellationToken cancellationToken)
        {
            var envName = args.Target;
            if (string.IsNullOrEmpty(envName))
            {
                CliOutput.Error("Environment name is required.");
                Console.WriteLine("Usage: vb codex settings <env-name>");
                return 1;
            }

            try
            {
                var settings = await codexEnv.GetSettings(envName, cancellationToken);

                CliOutput.Info($"Codex settings for environment '{envName}':");
                Console.WriteLine();

                var headers = new[] { "SETTING", "VALUE" };
                var rows = new List<string[]>
                {
                    new[] { "Model", string.IsNullOrEmpty(settings.Model) ? "(default)" : settings.Model },
                    new[] { "Sandbox", settings.Sandbox },
                    new[] { "Approval", settings.Approval },
                    new[] { "Full-Auto", settings.FullAuto ? "enabled" : "disabled" },
                    new[] { "Search", settings.Search ? "enabled" : "disabled" }
                };

                CliOutput.Table(headers, rows);
                return 0;
            }
            catch (Exception ex)
            {
                CliOutput.Error($"Failed to get settings: {ex.Message}");
                return 1;
            }
        }

        private static async Task<int> GetAsync(ParsedArgs args, ICodexLlmCliEnvironment codexEnv, CancellationToken cancellationToken)
        {
            var envName = args.Target;
            if (string.IsNullOrEmpty(envName))
            {
                CliOutput.Error("Environment name is required.");
                Console.WriteLine("Usage: vb codex get <env-name> --name <setting>");
                return 1;
            }

            var settingName = args.Name;
            if (string.IsNullOrEmpty(settingName))
            {
                CliOutput.Error("Setting name is required.");
                Console.WriteLine("Usage: vb codex get <env-name> --name <setting>");
                Console.WriteLine("Available settings: model, sandbox, approval, full-auto, search");
                return 1;
            }

            try
            {
                var settings = await codexEnv.GetSettings(envName, cancellationToken);

                var value = settingName.ToLowerInvariant() switch
                {
                    "model" => string.IsNullOrEmpty(settings.Model) ? "(default)" : settings.Model,
                    "sandbox" => settings.Sandbox,
                    "approval" => settings.Approval,
                    "full-auto" or "fullauto" => settings.FullAuto.ToString().ToLowerInvariant(),
                    "search" => settings.Search.ToString().ToLowerInvariant(),
                    _ => null
                };

                if (value == null)
                {
                    CliOutput.Error($"Unknown setting: {settingName}");
                    Console.WriteLine("Available settings: model, sandbox, approval, full-auto, search");
                    return 1;
                }

                Console.WriteLine(value);
                return 0;
            }
            catch (Exception ex)
            {
                CliOutput.Error($"Failed to get setting: {ex.Message}");
                return 1;
            }
        }

        private static async Task<int> SetAsync(ParsedArgs args, ICodexLlmCliEnvironment codexEnv, CancellationToken cancellationToken)
        {
            var envName = args.Target;
            if (string.IsNullOrEmpty(envName))
            {
                CliOutput.Error("Environment name is required.");
                Console.WriteLine("Usage: vb codex set <env-name> [options]");
                return 1;
            }

            // Check if any settings were provided
            bool hasModel = !string.IsNullOrEmpty(args.Model);
            bool hasSandbox = !string.IsNullOrEmpty(args.CodexSandbox);
            bool hasApproval = !string.IsNullOrEmpty(args.CodexApproval);
            bool hasFullAuto = args.FullAuto.HasValue;
            bool hasSearch = args.Search.HasValue;

            if (!hasModel && !hasSandbox && !hasApproval && !hasFullAuto && !hasSearch)
            {
                CliOutput.Error("No settings specified. Use options like --model, --sandbox, --approval, etc.");
                Console.WriteLine("Run 'vb codex --help' for available options.");
                return 1;
            }

            try
            {
                // Get current settings
                var settings = await codexEnv.GetSettings(envName, cancellationToken);

                // Apply changes
                var changes = new List<string>();

                if (hasModel)
                {
                    settings.Model = args.Model!;
                    changes.Add($"model={args.Model}");
                }

                if (hasSandbox)
                {
                    if (!ValidSandboxValues.Contains(args.CodexSandbox, StringComparer.OrdinalIgnoreCase))
                    {
                        CliOutput.Error($"Invalid sandbox value: {args.CodexSandbox}");
                        Console.WriteLine("Valid values: read-only, workspace-write, danger-full-access");
                        return 1;
                    }
                    settings.Sandbox = args.CodexSandbox!;
                    changes.Add($"sandbox={args.CodexSandbox}");

                    if (args.CodexSandbox!.Equals("danger-full-access", StringComparison.OrdinalIgnoreCase))
                    {
                        CliOutput.Warning("Sandbox set to danger-full-access - commands will have full system access!");
                    }
                }

                if (hasApproval)
                {
                    if (!ValidApprovalValues.Contains(args.CodexApproval, StringComparer.OrdinalIgnoreCase))
                    {
                        CliOutput.Error($"Invalid approval value: {args.CodexApproval}");
                        Console.WriteLine("Valid values: untrusted, on-failure, on-request, never");
                        return 1;
                    }
                    settings.Approval = args.CodexApproval!;
                    changes.Add($"approval={args.CodexApproval}");

                    if (args.CodexApproval!.Equals("never", StringComparison.OrdinalIgnoreCase))
                    {
                        CliOutput.Warning("Approval set to never - all commands will execute without confirmation!");
                    }
                }

                if (hasFullAuto)
                {
                    settings.FullAuto = args.FullAuto!.Value;
                    changes.Add($"full-auto={args.FullAuto.Value}");
                    if (args.FullAuto.Value)
                    {
                        CliOutput.Info("Full-auto mode enabled (approval=on-request, sandbox=workspace-write)");
                    }
                }

                if (hasSearch)
                {
                    settings.Search = args.Search!.Value;
                    changes.Add($"search={args.Search.Value}");
                }

                // Save settings
                await codexEnv.SaveSettings(envName, settings, cancellationToken);

                CliOutput.Success($"Updated Codex settings for '{envName}':");
                foreach (var change in changes)
                {
                    Console.WriteLine($"  {change}");
                }

                return 0;
            }
            catch (Exception ex)
            {
                CliOutput.Error($"Failed to update settings: {ex.Message}");
                return 1;
            }
        }
    }
}
