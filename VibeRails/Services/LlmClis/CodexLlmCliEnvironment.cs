using System.Text;
using System.Text.RegularExpressions;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Services.LlmClis
{
    public class CodexLlmCliEnvironment : BaseLlmCliEnvironment, ICodexLlmCliEnvironment
    {
        public CodexLlmCliEnvironment(IDbService dbService, IFileService fileService) : base(dbService, fileService) { }

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
            dto.Model = GetTomlValue(content, "model") ?? "";
            dto.Sandbox = GetTomlValue(content, "sandbox") ?? "read-only";
            dto.Approval = GetTomlValue(content, "approval") ?? "untrusted";
            dto.FullAuto = GetTomlBoolValue(content, "full_auto") ?? false;
            dto.Search = GetTomlBoolValue(content, "search") ?? false;

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

            // Update or add each setting
            existingContent = SetTomlValue(existingContent, "model", settings.Model);
            existingContent = SetTomlValue(existingContent, "sandbox", settings.Sandbox);
            existingContent = SetTomlValue(existingContent, "approval", settings.Approval);
            existingContent = SetTomlBoolValue(existingContent, "full_auto", settings.FullAuto);
            existingContent = SetTomlBoolValue(existingContent, "search", settings.Search);

            await _fileService.WriteAllTextAsync(configPath, existingContent, FileMode.Create, FileShare.None, cancellationToken);
        }

        private string GetSettingsFilePath(string envName)
        {
            var envBasePath = ParserConfigs.GetEnvPath();
            return Path.Combine(envBasePath, envName, GetConfigSubdirectory(), "config.toml");
        }

        private static string? GetTomlValue(string content, string key)
        {
            // Match: key = "value" or key = 'value' or key = value (unquoted)
            var pattern = $@"^\s*{Regex.Escape(key)}\s*=\s*[""']?([^""'\r\n]*)[""']?\s*$";
            var match = Regex.Match(content, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value.Trim() : null;
        }

        private static bool? GetTomlBoolValue(string content, string key)
        {
            var value = GetTomlValue(content, key);
            if (value == null) return null;
            return value.Equals("true", StringComparison.OrdinalIgnoreCase);
        }

        private static string SetTomlValue(string content, string key, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                // Remove the line if value is empty
                var removePattern = $@"^\s*{Regex.Escape(key)}\s*=.*$\r?\n?";
                return Regex.Replace(content, removePattern, "", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            }

            var pattern = $@"^(\s*){Regex.Escape(key)}\s*=.*$";
            var replacement = $"$1{key} = \"{value}\"";

            if (Regex.IsMatch(content, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                return Regex.Replace(content, pattern, replacement, RegexOptions.Multiline | RegexOptions.IgnoreCase);
            }
            else
            {
                // Add new line at the end
                var sb = new StringBuilder(content.TrimEnd());
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine($"{key} = \"{value}\"");
                return sb.ToString();
            }
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
