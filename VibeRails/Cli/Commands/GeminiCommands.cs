using Microsoft.Extensions.DependencyInjection;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Cli.Commands
{
    public static class GeminiCommands
    {
        public static async Task<int> ExecuteAsync(ParsedArgs args, IServiceProvider services, CancellationToken cancellationToken)
        {
            if (args.Help || string.IsNullOrEmpty(args.SubCommand))
            {
                return ShowHelp();
            }

            var geminiEnv = services.GetRequiredService<IGeminiLlmCliEnvironment>();

            return args.SubCommand.ToLowerInvariant() switch
            {
                "settings" => await SettingsAsync(args, geminiEnv, cancellationToken),
                "get" => await GetAsync(args, geminiEnv, cancellationToken),
                "set" => await SetAsync(args, geminiEnv, cancellationToken),
                "help" or "--help" => ShowHelp(),
                _ => ShowUnknownSubcommand(args.SubCommand)
            };
        }

        private static int ShowHelp()
        {
            CliOutput.Help(
                "vb gemini <subcommand> <env-name> [options]",
                "Configure Gemini CLI settings for environments",
                new Dictionary<string, string>
                {
                    ["settings <env>"] = "Show all Gemini settings for an environment",
                    ["get <env> <setting>"] = "Get a specific setting value",
                    ["set <env>"] = "Update Gemini settings for an environment"
                },
                new Dictionary<string, string>
                {
                    ["--theme <value>"] = "UI theme: Default, Dark, or Light",
                    ["--sandbox"] = "Enable sandbox mode (containerized execution)",
                    ["--no-sandbox"] = "Disable sandbox mode",
                    ["--auto-approve"] = "Enable auto-approve for safe operations",
                    ["--no-auto-approve"] = "Disable auto-approve",
                    ["--vim"] = "Enable Vim keybindings",
                    ["--no-vim"] = "Disable Vim keybindings",
                    ["--check-updates"] = "Enable automatic update checks",
                    ["--no-check-updates"] = "Disable automatic update checks",
                    ["--yolo"] = "Enable YOLO mode (auto-approve everything)",
                    ["--no-yolo"] = "Disable YOLO mode"
                }
            );

            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  vb gemini settings myenv");
            Console.WriteLine("  vb gemini get myenv theme");
            Console.WriteLine("  vb gemini set myenv --theme Dark --sandbox --no-yolo");
            Console.WriteLine("  vb gemini set myenv --yolo");

            return 0;
        }

        private static int ShowUnknownSubcommand(string subcommand)
        {
            CliOutput.Error($"Unknown subcommand: {subcommand}");
            Console.WriteLine("Run 'vb gemini --help' for available subcommands.");
            return 1;
        }

        private static async Task<int> SettingsAsync(ParsedArgs args, IGeminiLlmCliEnvironment geminiEnv, CancellationToken cancellationToken)
        {
            var envName = args.Target;
            if (string.IsNullOrEmpty(envName))
            {
                CliOutput.Error("Environment name is required.");
                Console.WriteLine("Usage: vb gemini settings <env-name>");
                return 1;
            }

            try
            {
                var settings = await geminiEnv.GetSettings(envName, cancellationToken);

                CliOutput.Info($"Gemini settings for environment '{envName}':");
                Console.WriteLine();

                var headers = new[] { "SETTING", "VALUE" };
                var rows = new List<string[]>
                {
                    new[] { "Theme", settings.Theme },
                    new[] { "Sandbox Mode", settings.SandboxEnabled ? "enabled" : "disabled" },
                    new[] { "Auto-Approve Tools", settings.AutoApproveTools ? "enabled" : "disabled" },
                    new[] { "Vim Mode", settings.VimMode ? "enabled" : "disabled" },
                    new[] { "Check for Updates", settings.CheckForUpdates ? "enabled" : "disabled" },
                    new[] { "YOLO Mode", settings.YoloMode ? "enabled (dangerous!)" : "disabled" }
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

        private static async Task<int> GetAsync(ParsedArgs args, IGeminiLlmCliEnvironment geminiEnv, CancellationToken cancellationToken)
        {
            var envName = args.Target;
            if (string.IsNullOrEmpty(envName))
            {
                CliOutput.Error("Environment name is required.");
                Console.WriteLine("Usage: vb gemini get <env-name> <setting>");
                return 1;
            }

            // The setting name should be the 4th positional argument (after "gemini get <env>")
            // But our parser puts it in SubCommand position. Let's use Name or parse differently.
            // Actually we need to handle this via parsing - the target is the env, and we need another arg for setting.
            // For simplicity, let's use --name for the setting name or check if there are extra args.

            var settingName = args.Name;
            if (string.IsNullOrEmpty(settingName))
            {
                CliOutput.Error("Setting name is required.");
                Console.WriteLine("Usage: vb gemini get <env-name> --name <setting>");
                Console.WriteLine("Available settings: theme, sandbox, auto-approve, vim, check-updates, yolo");
                return 1;
            }

            try
            {
                var settings = await geminiEnv.GetSettings(envName, cancellationToken);

                var value = settingName.ToLowerInvariant() switch
                {
                    "theme" => settings.Theme,
                    "sandbox" => settings.SandboxEnabled.ToString().ToLowerInvariant(),
                    "auto-approve" or "autoapprove" => settings.AutoApproveTools.ToString().ToLowerInvariant(),
                    "vim" => settings.VimMode.ToString().ToLowerInvariant(),
                    "check-updates" or "checkupdates" => settings.CheckForUpdates.ToString().ToLowerInvariant(),
                    "yolo" => settings.YoloMode.ToString().ToLowerInvariant(),
                    _ => null
                };

                if (value == null)
                {
                    CliOutput.Error($"Unknown setting: {settingName}");
                    Console.WriteLine("Available settings: theme, sandbox, auto-approve, vim, check-updates, yolo");
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

        private static async Task<int> SetAsync(ParsedArgs args, IGeminiLlmCliEnvironment geminiEnv, CancellationToken cancellationToken)
        {
            var envName = args.Target;
            if (string.IsNullOrEmpty(envName))
            {
                CliOutput.Error("Environment name is required.");
                Console.WriteLine("Usage: vb gemini set <env-name> [options]");
                return 1;
            }

            // Check if any settings were provided
            if (args.Theme == null && args.Sandbox == null && args.AutoApprove == null &&
                args.VimMode == null && args.CheckUpdates == null && args.Yolo == null)
            {
                CliOutput.Error("No settings specified. Use options like --theme, --sandbox, --yolo, etc.");
                Console.WriteLine("Run 'vb gemini --help' for available options.");
                return 1;
            }

            try
            {
                // Get current settings
                var settings = await geminiEnv.GetSettings(envName, cancellationToken);

                // Apply changes
                var changes = new List<string>();

                if (args.Theme != null)
                {
                    if (!new[] { "Default", "Dark", "Light" }.Contains(args.Theme, StringComparer.OrdinalIgnoreCase))
                    {
                        CliOutput.Error($"Invalid theme: {args.Theme}. Must be Default, Dark, or Light.");
                        return 1;
                    }
                    settings.Theme = args.Theme;
                    changes.Add($"theme={args.Theme}");
                }

                if (args.Sandbox.HasValue)
                {
                    settings.SandboxEnabled = args.Sandbox.Value;
                    changes.Add($"sandbox={args.Sandbox.Value}");
                }

                if (args.AutoApprove.HasValue)
                {
                    settings.AutoApproveTools = args.AutoApprove.Value;
                    changes.Add($"auto-approve={args.AutoApprove.Value}");
                }

                if (args.VimMode.HasValue)
                {
                    settings.VimMode = args.VimMode.Value;
                    changes.Add($"vim={args.VimMode.Value}");
                }

                if (args.CheckUpdates.HasValue)
                {
                    settings.CheckForUpdates = args.CheckUpdates.Value;
                    changes.Add($"check-updates={args.CheckUpdates.Value}");
                }

                if (args.Yolo.HasValue)
                {
                    settings.YoloMode = args.Yolo.Value;
                    changes.Add($"yolo={args.Yolo.Value}");
                    if (args.Yolo.Value)
                    {
                        CliOutput.Warning("YOLO mode enabled - all operations will be auto-approved!");
                    }
                }

                // Save settings
                await geminiEnv.SaveSettings(envName, settings, cancellationToken);

                CliOutput.Success($"Updated Gemini settings for '{envName}':");
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
