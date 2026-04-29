using Microsoft.Extensions.DependencyInjection;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Cli.Commands
{
    public static class CodexCommands
    {
        private static readonly string[] ValidApprovalValues = { "untrusted", "on-request", "never" };

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
                    ["--ask-for-approval, -a <value>"] = "Approval mode: untrusted, on-request, never",
                    ["--yolo"] = "Bypass approvals and sandboxing",
                    ["--full-auto"] = "Enable full-auto mode",
                    ["--no-alt-screen"] = "Disable alternate screen mode for the TUI",
                    ["--oss"] = "Use the local open source model provider",
                    ["--prompt <text>"] = "Optional text instruction to start the session"
                }
            );

            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  vb codex settings myenv");
            Console.WriteLine("  vb codex get myenv ask-for-approval");
            Console.WriteLine("  vb codex set myenv --ask-for-approval on-request --no-alt-screen");
            Console.WriteLine("  vb codex set myenv --yolo --prompt \"Investigate the failing tests\"");

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
                    new[] { "Ask For Approval", settings.AskForApproval },
                    new[] { "YOLO", settings.Yolo ? "enabled" : "disabled" },
                    new[] { "Full-Auto", settings.FullAuto ? "enabled" : "disabled" },
                    new[] { "No Alternate Screen", settings.NoAltScreen ? "enabled" : "disabled" },
                    new[] { "OSS Provider", settings.Oss ? "enabled" : "disabled" },
                    new[] { "Prompt", string.IsNullOrEmpty(settings.Prompt) ? "(none)" : Truncate(settings.Prompt, 80) }
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
                Console.WriteLine("Available settings: ask-for-approval, yolo, full-auto, no-alt-screen, oss, prompt");
                return 1;
            }

            try
            {
                var settings = await codexEnv.GetSettings(envName, cancellationToken);

                var value = settingName.ToLowerInvariant() switch
                {
                    "ask-for-approval" or "approval" => settings.AskForApproval,
                    "yolo" => settings.Yolo.ToString().ToLowerInvariant(),
                    "full-auto" or "fullauto" => settings.FullAuto.ToString().ToLowerInvariant(),
                    "no-alt-screen" or "noaltscreen" => settings.NoAltScreen.ToString().ToLowerInvariant(),
                    "oss" => settings.Oss.ToString().ToLowerInvariant(),
                    "prompt" => string.IsNullOrEmpty(settings.Prompt) ? "(none)" : settings.Prompt,
                    _ => null
                };

                if (value == null)
                {
                    CliOutput.Error($"Unknown setting: {settingName}");
                    Console.WriteLine("Available settings: ask-for-approval, yolo, full-auto, no-alt-screen, oss, prompt");
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

            var hasApproval = !string.IsNullOrEmpty(args.CodexApproval);
            var hasYolo = args.Yolo.HasValue;
            var hasFullAuto = args.FullAuto.HasValue;
            var hasNoAltScreen = args.NoAltScreen.HasValue;
            var hasOss = args.Oss.HasValue;
            var hasPrompt = args.Prompt != null;

            if (!hasApproval && !hasYolo && !hasFullAuto && !hasNoAltScreen && !hasOss && !hasPrompt)
            {
                CliOutput.Error("No settings specified. Use options like --ask-for-approval, --yolo, or --prompt.");
                Console.WriteLine("Run 'vb codex --help' for available options.");
                return 1;
            }

            try
            {
                var settings = await codexEnv.GetSettings(envName, cancellationToken);
                var changes = new List<string>();

                if (hasApproval)
                {
                    if (!ValidApprovalValues.Contains(args.CodexApproval, StringComparer.OrdinalIgnoreCase))
                    {
                        CliOutput.Error($"Invalid approval value: {args.CodexApproval}");
                        Console.WriteLine("Valid values: untrusted, on-request, never");
                        return 1;
                    }

                    settings.AskForApproval = args.CodexApproval!;
                    changes.Add($"ask-for-approval={args.CodexApproval}");

                    if (args.CodexApproval!.Equals("never", StringComparison.OrdinalIgnoreCase))
                    {
                        CliOutput.Warning("Approval set to never - commands can execute without confirmation.");
                    }
                }

                if (hasYolo)
                {
                    settings.Yolo = args.Yolo!.Value;
                    changes.Add($"yolo={args.Yolo.Value}");
                    if (args.Yolo.Value)
                    {
                        CliOutput.Warning("YOLO mode bypasses approvals and sandboxing.");
                    }
                }

                if (hasFullAuto)
                {
                    settings.FullAuto = args.FullAuto!.Value;
                    changes.Add($"full-auto={args.FullAuto.Value}");
                }

                if (hasNoAltScreen)
                {
                    settings.NoAltScreen = args.NoAltScreen!.Value;
                    changes.Add($"no-alt-screen={args.NoAltScreen.Value}");
                }

                if (hasOss)
                {
                    settings.Oss = args.Oss!.Value;
                    changes.Add($"oss={args.Oss.Value}");
                }

                if (hasPrompt)
                {
                    settings.Prompt = args.Prompt ?? "";
                    changes.Add(string.IsNullOrEmpty(settings.Prompt) ? "prompt=(none)" : "prompt=(set)");
                }

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

        private static string Truncate(string value, int maxLength)
        {
            if (value.Length <= maxLength)
            {
                return value;
            }

            return value[..Math.Max(0, maxLength - 3)] + "...";
        }
    }
}
