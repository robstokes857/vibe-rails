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

                    [model]
                    # default_model = "o3"

                    [approval]
                    # auto_approve = false

                    [sandbox]
                    # enabled = true
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

            // Parse TOML-style config (simple key = value format)
            dto.AskForApproval = NormalizeApproval(
                GetTomlValue(content, "ask_for_approval")
                ?? GetTomlValue(content, "approval")
                ?? "untrusted");
            dto.Yolo = GetTomlBoolValue(content, "yolo") ?? false;
            dto.FullAuto = GetTomlBoolValue(content, "full_auto") ?? false;
            dto.NoAltScreen = GetTomlBoolValue(content, "no_alt_screen") ?? false;
            dto.Oss = GetTomlBoolValue(content, "oss") ?? false;
            dto.Prompt = GetTomlValue(content, "prompt") ?? "";

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

            // `approval` is a legacy alias for `ask_for_approval`: GetSettings falls back
            // to it when the new key is missing, so we must clear it whenever we write the
            // new key — otherwise the legacy value would shadow the user's choice. Other
            // previously-managed fields (model, sandbox, search) have no VibeRails mapping
            // anymore; leave them untouched so a user's manual edits stick.
            existingContent = RemoveTomlValue(existingContent, "approval");

            // Update or add each supported setting.
            existingContent = SetTomlValue(existingContent, "ask_for_approval", NormalizeApproval(settings.AskForApproval));
            existingContent = SetTomlBoolValue(existingContent, "yolo", settings.Yolo);
            existingContent = SetTomlBoolValue(existingContent, "full_auto", settings.FullAuto);
            existingContent = SetTomlBoolValue(existingContent, "no_alt_screen", settings.NoAltScreen);
            existingContent = SetTomlBoolValue(existingContent, "oss", settings.Oss);
            existingContent = SetTomlValue(existingContent, "prompt", settings.Prompt);

            await _fileService.WriteAllTextAsync(configPath, existingContent, FileMode.Create, FileShare.None, cancellationToken);
        }

        private string GetSettingsFilePath(string envName)
        {
            var envBasePath = ParserConfigs.GetEnvPath();
            return Path.Combine(envBasePath, envName, GetConfigSubdirectory(), "config.toml");
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

        private static bool? GetTomlBoolValue(string content, string key)
        {
            var value = GetTomlValue(content, key);
            if (value == null) return null;
            return value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeApproval(string? value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "on-request" => "on-request",
                "never" => "never",
                "on-failure" => "on-request",
                _ => "untrusted"
            };
        }

        private static string RemoveTomlValue(string content, string key)
        {
            var removePattern = $@"^\s*{Regex.Escape(key)}\s*=.*$\r?\n?";
            return Regex.Replace(content, removePattern, "", RegexOptions.Multiline | RegexOptions.IgnoreCase);
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
            var pattern = $@"^(\s*){Regex.Escape(key)}\s*=.*$";

            if (Regex.IsMatch(content, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                return Regex.Replace(
                    content,
                    pattern,
                    m => $"{m.Groups[1].Value}{key} = {escapedValue}",
                    RegexOptions.Multiline | RegexOptions.IgnoreCase);
            }

            var sb = new StringBuilder(content.TrimEnd());
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine($"{key} = {escapedValue}");
            return sb.ToString();
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

        private static string SetTomlBoolValue(string content, string key, bool value)
        {
            var pattern = $@"^(\s*){Regex.Escape(key)}\s*=.*$";
            var replacement = $"$1{key} = {value.ToString().ToLowerInvariant()}";

            if (Regex.IsMatch(content, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                return Regex.Replace(content, pattern, replacement, RegexOptions.Multiline | RegexOptions.IgnoreCase);
            }
            else
            {
                // Add new line at the end
                var sb = new StringBuilder(content.TrimEnd());
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine($"{key} = {value.ToString().ToLowerInvariant()}");
                return sb.ToString();
            }
        }
    }
}
