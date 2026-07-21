using System.Text;
using System.Text.RegularExpressions;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Services.LlmClis
{
    public class CodexLlmCliEnvironment : BaseLlmCliEnvironment, ICodexLlmCliEnvironment
    {
        public CodexLlmCliEnvironment(IFileService fileService) : base(fileService) { }

        public override string GetConfigSubdirectory() => "codex";

        public override async Task CreateEnvironment(LLM_Environment environment, CancellationToken cancellationToken)
        {
            var configPath = Path.Combine(environment.Path, GetConfigSubdirectory());
            EnsureDirectoryExists(configPath);

            // Copy entire configuration from default Codex directory (~/.codex)
            var defaultCodexPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            CopyDirectoryRecursive(defaultCodexPath, configPath);

            // If config.toml still doesn't exist (no default Codex config), create a basic one
            var configFile = Path.Combine(configPath, "config.toml");
            if (!_fileService.FileExists(configFile))
            {
                var defaultConfig = """
                    # Codex CLI Configuration
                    # This is an isolated environment managed by Vibe Rails

                    # model = "gpt-5.6-sol"
                    # model_reasoning_effort = "medium"
                    # service_tier = "fast"

                    # approval_policy = "on-request"
                    # sandbox_mode = "workspace-write"
                    # tui.alternate_screen = "never"
                    # model_provider = "oss"

                    [features]
                    # fast_mode = true
                    """;
                await _fileService.WriteAllTextAsync(configFile, defaultConfig, FileMode.Create, FileShare.None, cancellationToken);
            }

            // If AGENTS.md still doesn't exist, create empty one
            var agentsFile = Path.Combine(configPath, "AGENTS.md");
            if (!_fileService.FileExists(agentsFile))
            {
                await _fileService.WriteAllTextAsync(agentsFile, "# Custom Instructions for Codex\n", FileMode.Create, FileShare.None, cancellationToken);
            }

            await Task.CompletedTask;
        }

        public async Task<CodexSettingsDto> GetSettings(string envName, CancellationToken cancellationToken)
        {
            var configPath = GetSettingsFilePath(envName);
            var dto = new CodexSettingsDto();

            if (!_fileService.FileExists(configPath))
            {
                return dto;
            }

            var content = await _fileService.ReadAllTextAsync(configPath, cancellationToken);

            // Parse TOML-style config (simple key = value format). Permission posture
            // (approval_policy / sandbox_mode) and model_provider are YOLO-or-nothing via
            // CustomArgs, so VibeRails neither reads nor edits them here.
            dto.Model = NormalizeModel(GetRootTomlValue(content, "model"));
            dto.Effort = NormalizeEffort(GetRootTomlValue(content, "model_reasoning_effort"));
            dto.FastMode = IsFastModeEnabled(content);
            dto.NoAltScreen = IsAlternateScreenDisabled(content);

            return dto;
        }

        public async Task SaveSettings(string envName, CodexSettingsDto settings, CancellationToken cancellationToken)
        {
            var configPath = GetSettingsFilePath(envName);

            // Ensure directory exists
            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory))
            {
                EnsureDirectoryExists(directory);
            }

            // Read existing content to preserve comments and unknown fields
            string existingContent = "";
            if (_fileService.FileExists(configPath))
            {
                existingContent = await _fileService.ReadAllTextAsync(configPath, cancellationToken);
            }

            // Clear legacy VibeRails-managed keys so saved environments use current Codex
            // config names only. approval_policy / sandbox_mode / model_provider are NOT in
            // this list on purpose: permission posture is YOLO-or-nothing via CustomArgs, so
            // we leave any user-set values for those keys untouched.
            existingContent = RemoveTomlValue(existingContent, "ask_for_approval");
            existingContent = RemoveTomlValue(existingContent, "approval");
            existingContent = RemoveTomlValue(existingContent, "yolo");
            existingContent = RemoveTomlValue(existingContent, "full_auto");
            existingContent = RemoveTomlValue(existingContent, "no_alt_screen");
            existingContent = RemoveTomlValue(existingContent, "oss");
            existingContent = RemoveTomlValue(existingContent, "prompt");

            existingContent = SetTomlValue(existingContent, "model", NormalizeModel(settings.Model));
            existingContent = SetTomlValue(existingContent, "model_reasoning_effort", NormalizeEffort(settings.Effort));
            existingContent = SetFastMode(existingContent, settings.FastMode);
            existingContent = SetAlternateScreen(existingContent, settings.NoAltScreen);

            await _fileService.WriteAllTextAsync(configPath, existingContent, FileMode.Create, FileShare.None, cancellationToken);
        }

        private string GetSettingsFilePath(string envName)
        {
            var envBasePath = ParserConfigs.GetEnvPath();
            var envDirectory = EnvironmentNameValidator.ResolveEnvironmentDirectory(envBasePath, envName);
            return Path.Combine(envDirectory, GetConfigSubdirectory(), "config.toml");
        }

        private static bool IsFastModeEnabled(string content)
        {
            var serviceTier = GetRootTomlValue(content, "service_tier");
            if (!string.Equals(serviceTier, "fast", StringComparison.OrdinalIgnoreCase))
                return false;

            var featureEnabled =
                GetRootTomlBoolValue(content, "features.fast_mode") ??
                GetTomlSectionBoolValue(content, "features", "fast_mode");

            return featureEnabled != false;
        }

        private static bool IsAlternateScreenDisabled(string content)
        {
            var value =
                GetRootTomlValue(content, "tui.alternate_screen") ??
                GetTomlSectionValue(content, "tui", "alternate_screen");

            return string.Equals(value, "never", StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetTomlValue(string content, string key)
        {
            // Three forms in order of preference:
            //   key = "basic string with \" and \\ escapes"
            //   key = 'literal string, no escapes, no embedded single quotes'
            //   key = bareword   (booleans, numbers, unquoted identifiers)
            var basicPattern = $@"^\s*{Regex.Escape(key)}\s*=\s*""((?:\\.|[^""\\])*)""\s*(?:#.*)?$";
            var basicMatch = Regex.Match(content, basicPattern, RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (basicMatch.Success)
                return UnescapeTomlBasicString(basicMatch.Groups[1].Value);

            var literalPattern = $@"^\s*{Regex.Escape(key)}\s*=\s*'([^'\r\n]*)'\s*(?:#.*)?$";
            var literalMatch = Regex.Match(content, literalPattern, RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (literalMatch.Success)
                return literalMatch.Groups[1].Value;

            var barePattern = $@"^\s*{Regex.Escape(key)}\s*=\s*([^""'\s#][^\r\n#]*?)\s*(?:#.*)?$";
            var bareMatch = Regex.Match(content, barePattern, RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return bareMatch.Success ? bareMatch.Groups[1].Value.Trim() : null;
        }

        private static string? GetTomlSectionValue(string content, string section, string key)
        {
            var sectionPattern = $@"(?ms)^\s*\[{Regex.Escape(section)}\]\s*(?<body>.*?)(?=^\s*\[|\z)";
            var sectionMatch = Regex.Match(content, sectionPattern, RegexOptions.IgnoreCase);
            if (!sectionMatch.Success)
                return null;

            return GetTomlValue(sectionMatch.Groups["body"].Value, key);
        }

        private static string UnescapeTomlBasicString(string raw)
        {
            var sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                if (raw[i] != '\\' || i + 1 >= raw.Length)
                {
                    sb.Append(raw[i]);
                    continue;
                }

                char next = raw[++i];
                switch (next)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case 'b': sb.Append('\b'); break;
                    case 't': sb.Append('\t'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'u' when i + 4 < raw.Length:
                        if (int.TryParse(raw.AsSpan(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out var cp4))
                            sb.Append((char)cp4);
                        i += 4;
                        break;
                    case 'U' when i + 8 < raw.Length:
                        if (int.TryParse(raw.AsSpan(i + 1, 8), System.Globalization.NumberStyles.HexNumber, null, out var cp8))
                            sb.Append(char.ConvertFromUtf32(cp8));
                        i += 8;
                        break;
                    default: sb.Append(next); break;
                }
            }
            return sb.ToString();
        }

        private static bool? GetTomlSectionBoolValue(string content, string section, string key)
        {
            var value = GetTomlSectionValue(content, section, key);
            if (value == null) return null;
            return value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeEffort(string? value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "minimal" => "minimal",
                "low" => "low",
                "medium" => "medium",
                "high" => "high",
                "xhigh" => "xhigh",
                "max" => "max",
                "ultra" => "ultra",
                _ => ""
            };
        }

        private static string NormalizeModel(string? value)
        {
            return value?.Trim() ?? "";
        }

        // TOML keys before the first [table] header belong to the root table. VibeRails only
        // manages root-table keys, so root edits/reads are restricted to this region — a save
        // must never delete or rewrite a key inside a user's [profiles.x] / nested table that
        // happens to share a name (e.g. `prompt`, `model`).
        private static (string Root, string Tail) SplitRootTable(string content)
        {
            var firstTable = Regex.Match(content, @"(?m)^\s*\[");
            return firstTable.Success
                ? (content[..firstTable.Index], content[firstTable.Index..])
                : (content, "");
        }

        private static string? GetRootTomlValue(string content, string key)
            => GetTomlValue(SplitRootTable(content).Root, key);

        private static bool? GetRootTomlBoolValue(string content, string key)
        {
            var value = GetRootTomlValue(content, key);
            if (value == null) return null;
            return value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static string RemoveTomlValue(string content, string key)
        {
            var (root, rest) = SplitRootTable(content);
            var removePattern = $@"^\s*{Regex.Escape(key)}\s*=.*$\r?\n?";
            root = Regex.Replace(root, removePattern, "", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return root + rest;
        }

        private static string SetTomlValue(string content, string key, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return RemoveTomlValue(content, key);
            }

            // Escape the value as a TOML basic string so user input containing quotes,
            // backslashes, or newlines can't break out of the value or inject keys.
            // Use a MatchEvaluator (not a replacement string) so `$1`/`$0`/etc. inside
            // the user's value aren't interpreted by Regex.Replace.
            var escapedValue = EscapeTomlBasicString(value);
            var (root, rest) = SplitRootTable(content);
            var pattern = $@"^(\s*){Regex.Escape(key)}\s*=.*$";

            if (Regex.IsMatch(root, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                root = Regex.Replace(
                    root,
                    pattern,
                    m => $"{m.Groups[1].Value}{key} = {escapedValue}",
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);
                return root + rest;
            }

            // AddRootTomlLine inserts before the first table, i.e. into the root region.
            return AddRootTomlLine(content, $"{key} = {escapedValue}");
        }

        private static string EscapeTomlBasicString(string value)
        {
            var sb = new StringBuilder(value.Length + 2);
            sb.Append('"');
            foreach (var ch in value)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"':  sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\r': sb.Append("\\r"); break;
                    default:
                        if (ch < 0x20 || ch == 0x7f)
                            sb.Append($"\\u{(int)ch:X4}");
                        else
                            sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string SetFastMode(string content, bool enabled)
        {
            if (enabled)
            {
                content = SetTomlValue(content, "service_tier", "fast");
                // Strip any root dotted form so we don't leave both `features.fast_mode` and a
                // [features] fast_mode key (a duplicate-key TOML error).
                content = RemoveTomlValue(content, "features.fast_mode");
                return SetTomlSectionBoolValue(content, "features", "fast_mode", true);
            }

            var serviceTier = GetRootTomlValue(content, "service_tier");
            if (string.Equals(serviceTier, "fast", StringComparison.OrdinalIgnoreCase))
                content = RemoveTomlValue(content, "service_tier");

            content = RemoveTomlValue(content, "features.fast_mode");
            return RemoveTomlSectionValue(content, "features", "fast_mode");
        }

        private static string SetAlternateScreen(string content, bool disabled)
        {
            // Always clear any root dotted form first; we manage this via the [tui] section.
            // Leaving a dotted `tui.alternate_screen` next to a [tui] block would be a
            // duplicate-key TOML error, and IsAlternateScreenDisabled would read the stale value.
            content = RemoveTomlValue(content, "tui.alternate_screen");

            return disabled
                ? SetTomlSectionValue(content, "tui", "alternate_screen", "never")
                : RemoveTomlSectionValue(content, "tui", "alternate_screen");
        }

        private static string SetTomlSectionBoolValue(string content, string section, string key, bool value)
        {
            var rendered = $"{key} = {value.ToString().ToLowerInvariant()}";
            var sectionPattern = $@"(?ms)^\s*\[{Regex.Escape(section)}\]\s*(?<body>.*?)(?=^\s*\[|\z)";
            var sectionMatch = Regex.Match(content, sectionPattern, RegexOptions.IgnoreCase);

            if (!sectionMatch.Success)
            {
                var sb = new StringBuilder(content.TrimEnd());
                if (sb.Length > 0) sb.AppendLine().AppendLine();
                sb.AppendLine($"[{section}]");
                sb.AppendLine(rendered);
                return sb.ToString();
            }

            var bodyGroup = sectionMatch.Groups["body"];
            var body = bodyGroup.Value;
            var keyPattern = $@"^(\s*){Regex.Escape(key)}\s*=.*$";

            if (Regex.IsMatch(body, keyPattern, RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                body = Regex.Replace(
                    body,
                    keyPattern,
                    m => $"{m.Groups[1].Value}{rendered}",
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);
            }
            else
            {
                var sb = new StringBuilder(body.TrimEnd());
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine(rendered);
                body = sb.ToString();
            }

            return content[..bodyGroup.Index] + body + content[(bodyGroup.Index + bodyGroup.Length)..];
        }

        private static string SetTomlSectionValue(string content, string section, string key, string value)
        {
            if (string.IsNullOrEmpty(value))
                return RemoveTomlSectionValue(content, section, key);

            var rendered = $"{key} = {EscapeTomlBasicString(value)}";
            var sectionPattern = $@"(?ms)^\s*\[{Regex.Escape(section)}\]\s*(?<body>.*?)(?=^\s*\[|\z)";
            var sectionMatch = Regex.Match(content, sectionPattern, RegexOptions.IgnoreCase);

            if (!sectionMatch.Success)
            {
                var sb = new StringBuilder(content.TrimEnd());
                if (sb.Length > 0) sb.AppendLine().AppendLine();
                sb.AppendLine($"[{section}]");
                sb.AppendLine(rendered);
                return sb.ToString();
            }

            var bodyGroup = sectionMatch.Groups["body"];
            var body = bodyGroup.Value;
            var keyPattern = $@"^(\s*){Regex.Escape(key)}\s*=.*$";

            if (Regex.IsMatch(body, keyPattern, RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                body = Regex.Replace(
                    body,
                    keyPattern,
                    m => $"{m.Groups[1].Value}{rendered}",
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);
            }
            else
            {
                var sb = new StringBuilder(body.TrimEnd());
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine(rendered);
                body = sb.ToString();
            }

            return content[..bodyGroup.Index] + body + content[(bodyGroup.Index + bodyGroup.Length)..];
        }

        private static string RemoveTomlSectionValue(string content, string section, string key)
        {
            var sectionPattern = $@"(?ms)^\s*\[{Regex.Escape(section)}\]\s*(?<body>.*?)(?=^\s*\[|\z)";
            var sectionMatch = Regex.Match(content, sectionPattern, RegexOptions.IgnoreCase);
            if (!sectionMatch.Success)
                return content;

            var bodyGroup = sectionMatch.Groups["body"];
            var keyPattern = $@"^\s*{Regex.Escape(key)}\s*=.*$\r?\n?";
            var body = Regex.Replace(bodyGroup.Value, keyPattern, "", RegexOptions.Multiline | RegexOptions.IgnoreCase);

            return content[..bodyGroup.Index] + body + content[(bodyGroup.Index + bodyGroup.Length)..];
        }

        private static string AddRootTomlLine(string content, string line)
        {
            var trimmed = content.TrimEnd();
            if (trimmed.Length == 0)
                return line + Environment.NewLine;

            var firstTable = Regex.Match(trimmed, @"(?m)^\s*\[");
            if (!firstTable.Success)
                return trimmed + Environment.NewLine + line + Environment.NewLine;

            var before = trimmed[..firstTable.Index].TrimEnd();
            var after = trimmed[firstTable.Index..].TrimStart('\r', '\n');
            var sb = new StringBuilder();

            if (before.Length > 0)
            {
                sb.AppendLine(before);
            }

            sb.AppendLine(line);

            if (after.Length > 0)
            {
                sb.AppendLine();
                sb.Append(after);
            }

            return sb.ToString();
        }
    }
}
