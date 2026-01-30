using Microsoft.Extensions.DependencyInjection;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Cli.Commands
{
    public static class ClaudeCommands
    {
        private static readonly string[] ValidPermissionModes = { "default", "plan", "bypassPermissions" };

        public static async Task<int> ExecuteAsync(ParsedArgs args, IServiceProvider services, CancellationToken cancellationToken)
        {
            if (args.Help || string.IsNullOrEmpty(args.SubCommand))
            {
                return ShowHelp();
            }

            var claudeEnv = services.GetRequiredService<IClaudeLlmCliEnvironment>();

            return args.SubCommand.ToLowerInvariant() switch
            {
                "settings" => await SettingsAsync(args, claudeEnv, cancellationToken),
                "get" => await GetAsync(args, claudeEnv, cancellationToken),
                "set" => await SetAsync(args, claudeEnv, cancellationToken),
                "help" or "--help" => ShowHelp(),
                _ => ShowUnknownSubcommand(args.SubCommand)
            };
        }

        private static int ShowHelp()
        {
            CliOutput.Help(
                "vb claude <subcommand> <env-name> [options]",
                "Configure Claude CLI settings for environments",
                new Dictionary<string, string>
                {
                    ["settings <env>"] = "Show all Claude settings for an environment",
                    ["get <env> <setting>"] = "Get a specific setting value",
                    ["set <env>"] = "Update Claude settings for an environment"
                },
                new Dictionary<string, string>
                {
                    ["--model <value>"] = "Model to use (sonnet, opus, haiku, or full name)",
                    ["--permission-mode <value>"] = "Permission mode: default, plan, bypassPermissions",
                    ["--allowed-tools <value>"] = "Comma-separated list of tools to auto-approve",
                    ["--disallowed-tools <value>"] = "Comma-separated list of tools to disable",
                    ["--skip-permissions"] = "Skip all permission prompts (dangerous!)",
                    ["--no-skip-permissions"] = "Require permission prompts",
                    ["--verbose"] = "Enable verbose logging",
                    ["--no-verbose"] = "Disable verbose logging"
                }
            );

            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  vb claude settings myenv");
            Console.WriteLine("  vb claude get myenv model");
            Console.WriteLine("  vb claude set myenv --model opus --verbose");
            Console.WriteLine("  vb claude set myenv --permission-mode plan");

            return 0;
        }

        private static int ShowUnknownSubcommand(string subcommand)
        {
            CliOutput.Error($"Unknown subcommand: {subcommand}");
            Console.WriteLine("Run 'vb claude --help' for available subcommands.");
            return 1;
        }

        private static async Task<int> SettingsAsync(ParsedArgs args, IClaudeLlmCliEnvironment claudeEnv, CancellationToken cancellationToken)
        {
            var envName = args.Target;
            if (string.IsNullOrEmpty(envName))
            {
                CliOutput.Error("Environment name is required.");
                Console.WriteLine("Usage: vb claude settings <env-name>");
                return 1;
            }

            try
            {
                var settings = await claudeEnv.GetSettings(envName, cancellationToken);

                CliOutput.Info($"Claude settings for environment '{envName}':");
                Console.WriteLine();

                var headers = new[] { "SETTING", "VALUE" };
                var rows = new List<string[]>
                {
                    new[] { "Model", string.IsNullOrEmpty(settings.Model) ? "(default)" : settings.Model },
                    new[] { "Permission Mode", settings.PermissionMode },
                    new[] { "Allowed Tools", string.IsNullOrEmpty(settings.AllowedTools) ? "(none)" : settings.AllowedTools },
                    new[] { "Disallowed Tools", string.IsNullOrEmpty(settings.DisallowedTools) ? "(none)" : settings.DisallowedTools },
                    new[] { "Skip Permissions", settings.SkipPermissions ? "enabled" : "disabled" },
                    new[] { "Verbose", settings.Verbose ? "enabled" : "disabled" }
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

        private static async Task<int> GetAsync(ParsedArgs args, IClaudeLlmCliEnvironment claudeEnv, CancellationToken cancellationToken)
        {
            var envName = args.Target;
            if (string.IsNullOrEmpty(envName))
            {
                CliOutput.Error("Environment name is required.");
                Console.WriteLine("Usage: vb claude get <env-name> --name <setting>");
                return 1;
            }

            var settingName = args.Name;
            if (string.IsNullOrEmpty(settingName))
            {
                CliOutput.Error("Setting name is required.");
                Console.WriteLine("Usage: vb claude get <env-name> --name <setting>");
                Console.WriteLine("Available settings: model, permission-mode, allowed-tools, disallowed-tools, skip-permissions, verbose");
                return 1;
            }

            try
            {
                var settings = await claudeEnv.GetSettings(envName, cancellationToken);

                var value = settingName.ToLowerInvariant() switch
                {
                    "model" => string.IsNullOrEmpty(settings.Model) ? "(default)" : settings.Model,
                    "permission-mode" or "permissionmode" => settings.PermissionMode,
                    "allowed-tools" or "allowedtools" => string.IsNullOrEmpty(settings.AllowedTools) ? "(none)" : settings.AllowedTools,
                    "disallowed-tools" or "disallowedtools" => string.IsNullOrEmpty(settings.DisallowedTools) ? "(none)" : settings.DisallowedTools,
                    "skip-permissions" or "skippermissions" => settings.SkipPermissions.ToString().ToLowerInvariant(),
                    "verbose" => settings.Verbose.ToString().ToLowerInvariant(),
                    _ => null
                };

                if (value == null)
                {
                    CliOutput.Error($"Unknown setting: {settingName}");
                    Console.WriteLine("Available settings: model, permission-mode, allowed-tools, disallowed-tools, skip-permissions, verbose");
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

        private static async Task<int> SetAsync(ParsedArgs args, IClaudeLlmCliEnvironment claudeEnv, CancellationToken cancellationToken)
        {
            var envName = args.Target;
            if (string.IsNullOrEmpty(envName))
            {
                CliOutput.Error("Environment name is required.");
                Console.WriteLine("Usage: vb claude set <env-name> [options]");
                return 1;
            }

            // Check if any settings were provided
            bool hasModel = !string.IsNullOrEmpty(args.Model);
            bool hasPermissionMode = !string.IsNullOrEmpty(args.ClaudePermissionMode);
            bool hasAllowedTools = !string.IsNullOrEmpty(args.ClaudeAllowedTools);
            bool hasDisallowedTools = !string.IsNullOrEmpty(args.ClaudeDisallowedTools);
            bool hasSkipPermissions = args.ClaudeSkipPermissions.HasValue;
            bool hasVerbose = args.Verbose;

            if (!hasModel && !hasPermissionMode && !hasAllowedTools && !hasDisallowedTools && !hasSkipPermissions && !hasVerbose)
            {
                CliOutput.Error("No settings specified. Use options like --model, --permission-mode, --verbose, etc.");
                Console.WriteLine("Run 'vb claude --help' for available options.");
                return 1;
            }

            try
            {
                // Get current settings
                var settings = await claudeEnv.GetSettings(envName, cancellationToken);

                // Apply changes
                var changes = new List<string>();

                if (hasModel)
                {
                    settings.Model = args.Model!;
                    changes.Add($"model={args.Model}");
                }

                if (hasPermissionMode)
                {
                    if (!ValidPermissionModes.Contains(args.ClaudePermissionMode, StringComparer.OrdinalIgnoreCase))
                    {
                        CliOutput.Error($"Invalid permission mode: {args.ClaudePermissionMode}");
                        Console.WriteLine("Valid values: default, plan, bypassPermissions");
                        return 1;
                    }
                    settings.PermissionMode = args.ClaudePermissionMode!;
                    changes.Add($"permission-mode={args.ClaudePermissionMode}");

                    if (args.ClaudePermissionMode!.Equals("bypassPermissions", StringComparison.OrdinalIgnoreCase))
                    {
                        CliOutput.Warning("Permission mode set to bypassPermissions - all operations will execute without approval!");
                    }
                }

                if (hasAllowedTools)
                {
                    settings.AllowedTools = args.ClaudeAllowedTools!;
                    changes.Add($"allowed-tools={args.ClaudeAllowedTools}");
                }

                if (hasDisallowedTools)
                {
                    settings.DisallowedTools = args.ClaudeDisallowedTools!;
                    changes.Add($"disallowed-tools={args.ClaudeDisallowedTools}");
                }

                if (hasSkipPermissions)
                {
                    settings.SkipPermissions = args.ClaudeSkipPermissions!.Value;
                    changes.Add($"skip-permissions={args.ClaudeSkipPermissions.Value}");
                    if (args.ClaudeSkipPermissions.Value)
                    {
                        CliOutput.Warning("Skip permissions enabled - all operations will execute without prompting!");
                    }
                }

                if (hasVerbose)
                {
                    settings.Verbose = true;
                    changes.Add("verbose=true");
                }

                // Save settings
                await claudeEnv.SaveSettings(envName, settings, cancellationToken);

                CliOutput.Success($"Updated Claude settings for '{envName}':");
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
