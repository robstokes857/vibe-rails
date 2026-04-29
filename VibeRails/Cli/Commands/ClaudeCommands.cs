using Microsoft.Extensions.DependencyInjection;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Cli.Commands
{
    public static class ClaudeCommands
    {
        private static readonly string[] ValidEfforts = { "low", "medium", "high", "xhigh", "max" };
        private static readonly string[] ValidPermissionModes = { "default", "acceptEdits", "plan", "auto", "dontAsk", "bypassPermissions" };

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
                    ["--effort <value>"] = "Effort level: low, medium, high, xhigh, max",
                    ["--no-session-persistence"] = "Disable session persistence for print-mode runs",
                    ["--permission-mode <value>"] = "Permission mode: default, acceptEdits, plan, auto, dontAsk, bypassPermissions",
                    ["--system-prompt <text>"] = "Replace the entire system prompt",
                    ["--allow-dangerously-skip-permissions"] = "Allow switching to bypassPermissions later",
                    ["--dangerously-load-development-channels <entries>"] = "Load local development channels",
                    ["--dangerously-skip-permissions"] = "Start with permission prompts bypassed",
                    ["--allowedTools <entries>"] = "Tools that execute without prompting",
                    ["--append-system-prompt <text>"] = "Append text to the default system prompt",
                    ["--bare"] = "Enable minimal bare mode",
                    ["--betas <entries>"] = "Beta headers to include in API requests",
                    ["--channels <entries>"] = "MCP server channels to listen for",
                    ["--debug [filter]"] = "Enable debug mode with optional category filter"
                }
            );

            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  vb claude settings myenv");
            Console.WriteLine("  vb claude get myenv permission-mode");
            Console.WriteLine("  vb claude set myenv --effort high --permission-mode plan");
            Console.WriteLine("  vb claude set myenv --allowedTools \"Bash(git log *)\" --debug api,mcp");

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
                    new[] { "Effort", string.IsNullOrEmpty(settings.Effort) ? "(default)" : settings.Effort },
                    new[] { "No Session Persistence", settings.NoSessionPersistence ? "enabled" : "disabled" },
                    new[] { "Permission Mode", settings.PermissionMode },
                    new[] { "System Prompt", string.IsNullOrEmpty(settings.SystemPrompt) ? "(none)" : Truncate(settings.SystemPrompt, 80) },
                    new[] { "Allow Dangerous Skip", settings.AllowDangerouslySkipPermissions ? "enabled" : "disabled" },
                    new[] { "Development Channels", string.IsNullOrEmpty(settings.DangerouslyLoadDevelopmentChannels) ? "(none)" : settings.DangerouslyLoadDevelopmentChannels },
                    new[] { "Dangerously Skip Permissions", settings.DangerouslySkipPermissions ? "enabled" : "disabled" },
                    new[] { "Allowed Tools", string.IsNullOrEmpty(settings.AllowedTools) ? "(none)" : settings.AllowedTools },
                    new[] { "Append System Prompt", string.IsNullOrEmpty(settings.AppendSystemPrompt) ? "(none)" : Truncate(settings.AppendSystemPrompt, 80) },
                    new[] { "Bare", settings.Bare ? "enabled" : "disabled" },
                    new[] { "Betas", string.IsNullOrEmpty(settings.Betas) ? "(none)" : settings.Betas },
                    new[] { "Channels", string.IsNullOrEmpty(settings.Channels) ? "(none)" : settings.Channels },
                    new[] { "Debug", settings.Debug ? "enabled" : "disabled" },
                    new[] { "Debug Filter", string.IsNullOrEmpty(settings.DebugFilter) ? "(none)" : settings.DebugFilter }
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
                Console.WriteLine("Available settings: effort, no-session-persistence, permission-mode, system-prompt, allow-dangerously-skip-permissions, dangerously-load-development-channels, dangerously-skip-permissions, allowedTools, append-system-prompt, bare, betas, channels, debug, debug-filter");
                return 1;
            }

            try
            {
                var settings = await claudeEnv.GetSettings(envName, cancellationToken);

                var value = settingName.ToLowerInvariant() switch
                {
                    "effort" => string.IsNullOrEmpty(settings.Effort) ? "(default)" : settings.Effort,
                    "no-session-persistence" or "nosessionpersistence" => settings.NoSessionPersistence.ToString().ToLowerInvariant(),
                    "permission-mode" or "permissionmode" => settings.PermissionMode,
                    "system-prompt" or "systemprompt" => string.IsNullOrEmpty(settings.SystemPrompt) ? "(none)" : settings.SystemPrompt,
                    "allow-dangerously-skip-permissions" or "allowdangerouslyskippermissions" => settings.AllowDangerouslySkipPermissions.ToString().ToLowerInvariant(),
                    "dangerously-load-development-channels" or "dangerouslyloaddevelopmentchannels" => string.IsNullOrEmpty(settings.DangerouslyLoadDevelopmentChannels) ? "(none)" : settings.DangerouslyLoadDevelopmentChannels,
                    "dangerously-skip-permissions" or "dangerouslyskippermissions" => settings.DangerouslySkipPermissions.ToString().ToLowerInvariant(),
                    "allowedtools" or "allowed-tools" => string.IsNullOrEmpty(settings.AllowedTools) ? "(none)" : settings.AllowedTools,
                    "append-system-prompt" or "appendsystemprompt" => string.IsNullOrEmpty(settings.AppendSystemPrompt) ? "(none)" : settings.AppendSystemPrompt,
                    "bare" => settings.Bare.ToString().ToLowerInvariant(),
                    "betas" => string.IsNullOrEmpty(settings.Betas) ? "(none)" : settings.Betas,
                    "channels" => string.IsNullOrEmpty(settings.Channels) ? "(none)" : settings.Channels,
                    "debug" => settings.Debug.ToString().ToLowerInvariant(),
                    "debug-filter" or "debugfilter" => string.IsNullOrEmpty(settings.DebugFilter) ? "(none)" : settings.DebugFilter,
                    _ => null
                };

                if (value == null)
                {
                    CliOutput.Error($"Unknown setting: {settingName}");
                    Console.WriteLine("Available settings: effort, no-session-persistence, permission-mode, system-prompt, allow-dangerously-skip-permissions, dangerously-load-development-channels, dangerously-skip-permissions, allowedTools, append-system-prompt, bare, betas, channels, debug, debug-filter");
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

            var hasEffort = !string.IsNullOrEmpty(args.ClaudeEffort);
            var hasNoSessionPersistence = args.ClaudeNoSessionPersistence.HasValue;
            var hasPermissionMode = !string.IsNullOrEmpty(args.ClaudePermissionMode);
            var hasSystemPrompt = args.ClaudeSystemPrompt != null;
            var hasAllowDangerouslySkipPermissions = args.ClaudeAllowDangerouslySkipPermissions.HasValue;
            var hasDangerouslyLoadDevelopmentChannels = args.ClaudeDangerouslyLoadDevelopmentChannels != null;
            var hasDangerouslySkipPermissions = args.ClaudeDangerouslySkipPermissions.HasValue;
            var hasAllowedTools = args.ClaudeAllowedTools != null;
            var hasAppendSystemPrompt = args.ClaudeAppendSystemPrompt != null;
            var hasBare = args.ClaudeBare.HasValue;
            var hasBetas = args.ClaudeBetas != null;
            var hasChannels = args.ClaudeChannels != null;
            var hasDebug = args.ClaudeDebug.HasValue || args.ClaudeDebugFilter != null;

            if (!hasEffort &&
                !hasNoSessionPersistence &&
                !hasPermissionMode &&
                !hasSystemPrompt &&
                !hasAllowDangerouslySkipPermissions &&
                !hasDangerouslyLoadDevelopmentChannels &&
                !hasDangerouslySkipPermissions &&
                !hasAllowedTools &&
                !hasAppendSystemPrompt &&
                !hasBare &&
                !hasBetas &&
                !hasChannels &&
                !hasDebug)
            {
                CliOutput.Error("No settings specified. Use options like --effort, --permission-mode, or --allowedTools.");
                Console.WriteLine("Run 'vb claude --help' for available options.");
                return 1;
            }

            try
            {
                var settings = await claudeEnv.GetSettings(envName, cancellationToken);
                var changes = new List<string>();

                if (hasEffort)
                {
                    if (!ValidEfforts.Contains(args.ClaudeEffort, StringComparer.OrdinalIgnoreCase))
                    {
                        CliOutput.Error($"Invalid effort value: {args.ClaudeEffort}");
                        Console.WriteLine("Valid values: low, medium, high, xhigh, max");
                        return 1;
                    }

                    settings.Effort = args.ClaudeEffort!;
                    changes.Add($"effort={args.ClaudeEffort}");
                }

                if (hasNoSessionPersistence)
                {
                    settings.NoSessionPersistence = args.ClaudeNoSessionPersistence!.Value;
                    changes.Add($"no-session-persistence={args.ClaudeNoSessionPersistence.Value}");
                }

                if (hasPermissionMode)
                {
                    if (!ValidPermissionModes.Contains(args.ClaudePermissionMode, StringComparer.OrdinalIgnoreCase))
                    {
                        CliOutput.Error($"Invalid permission mode: {args.ClaudePermissionMode}");
                        Console.WriteLine("Valid values: default, acceptEdits, plan, auto, dontAsk, bypassPermissions");
                        return 1;
                    }

                    settings.PermissionMode = args.ClaudePermissionMode!;
                    changes.Add($"permission-mode={args.ClaudePermissionMode}");

                    if (args.ClaudePermissionMode!.Equals("bypassPermissions", StringComparison.OrdinalIgnoreCase))
                    {
                        CliOutput.Warning("Permission mode set to bypassPermissions - operations can execute without approval.");
                    }
                }

                if (hasSystemPrompt)
                {
                    settings.SystemPrompt = args.ClaudeSystemPrompt ?? "";
                    changes.Add(string.IsNullOrEmpty(settings.SystemPrompt) ? "system-prompt=(none)" : "system-prompt=(set)");
                }

                if (hasAllowDangerouslySkipPermissions)
                {
                    settings.AllowDangerouslySkipPermissions = args.ClaudeAllowDangerouslySkipPermissions!.Value;
                    changes.Add($"allow-dangerously-skip-permissions={args.ClaudeAllowDangerouslySkipPermissions.Value}");
                }

                if (hasDangerouslyLoadDevelopmentChannels)
                {
                    settings.DangerouslyLoadDevelopmentChannels = args.ClaudeDangerouslyLoadDevelopmentChannels ?? "";
                    changes.Add(string.IsNullOrEmpty(settings.DangerouslyLoadDevelopmentChannels) ? "dangerously-load-development-channels=(none)" : $"dangerously-load-development-channels={settings.DangerouslyLoadDevelopmentChannels}");
                }

                if (hasDangerouslySkipPermissions)
                {
                    settings.DangerouslySkipPermissions = args.ClaudeDangerouslySkipPermissions!.Value;
                    changes.Add($"dangerously-skip-permissions={args.ClaudeDangerouslySkipPermissions.Value}");
                    if (args.ClaudeDangerouslySkipPermissions.Value)
                    {
                        CliOutput.Warning("Dangerously skip permissions enabled - prompts will be bypassed.");
                    }
                }

                if (hasAllowedTools)
                {
                    settings.AllowedTools = args.ClaudeAllowedTools ?? "";
                    changes.Add(string.IsNullOrEmpty(settings.AllowedTools) ? "allowedTools=(none)" : $"allowedTools={settings.AllowedTools}");
                }

                if (hasAppendSystemPrompt)
                {
                    settings.AppendSystemPrompt = args.ClaudeAppendSystemPrompt ?? "";
                    changes.Add(string.IsNullOrEmpty(settings.AppendSystemPrompt) ? "append-system-prompt=(none)" : "append-system-prompt=(set)");
                }

                if (hasBare)
                {
                    settings.Bare = args.ClaudeBare!.Value;
                    changes.Add($"bare={args.ClaudeBare.Value}");
                }

                if (hasBetas)
                {
                    settings.Betas = args.ClaudeBetas ?? "";
                    changes.Add(string.IsNullOrEmpty(settings.Betas) ? "betas=(none)" : $"betas={settings.Betas}");
                }

                if (hasChannels)
                {
                    settings.Channels = args.ClaudeChannels ?? "";
                    changes.Add(string.IsNullOrEmpty(settings.Channels) ? "channels=(none)" : $"channels={settings.Channels}");
                }

                if (hasDebug)
                {
                    settings.Debug = args.ClaudeDebug ?? settings.Debug;
                    settings.DebugFilter = args.ClaudeDebugFilter ?? settings.DebugFilter;
                    changes.Add(settings.Debug ? "debug=true" : "debug=false");
                    if (!string.IsNullOrEmpty(settings.DebugFilter))
                    {
                        changes.Add($"debug-filter={settings.DebugFilter}");
                    }
                }

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
