using System.Text.Json;
using System.Text.Json.Nodes;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Services.LlmClis
{
    public class ClaudeLlmCliEnvironment : BaseLlmCliEnvironment, IClaudeLlmCliEnvironment
    {
        public ClaudeLlmCliEnvironment(IFileService fileService) : base(fileService) { }

        public override string GetConfigSubdirectory() => "claude";

        public override async Task CreateEnvironment(LLM_Environment environment, CancellationToken cancellationToken)
        {
            var configPath = Path.Combine(environment.Path, GetConfigSubdirectory());
            EnsureDirectoryExists(configPath);

            // Copy entire configuration from default Claude directory (~/.claude), excluding backups
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var defaultClaudePath = Path.Combine(userProfile, ".claude");
            CopyDirectoryRecursive(defaultClaudePath, configPath, excludedDirNames: ["backups"]);

            // Explicitly copy config.json (contains API key) if it wasn't copied
            var defaultConfigFile = Path.Combine(defaultClaudePath, "config.json");
            var envConfigFile = Path.Combine(configPath, "config.json");
            if (_fileService.FileExists(defaultConfigFile) && !_fileService.FileExists(envConfigFile))
            {
                _fileService.CopyFile(defaultConfigFile, envConfigFile, overwrite: false);
            }

            // Copy ~/.claude.json (auth tokens) - lives in user profile root, not inside ~/.claude
            var defaultClaudeJsonFile = Path.Combine(userProfile, ".claude.json");
            var envClaudeJsonFile = Path.Combine(configPath, ".claude.json");
            if (_fileService.FileExists(defaultClaudeJsonFile) && !_fileService.FileExists(envClaudeJsonFile))
            {
                _fileService.CopyFile(defaultClaudeJsonFile, envClaudeJsonFile, overwrite: false);
            }

            // If settings.json still doesn't exist (no default Claude config), create a basic one
            var settingsFile = Path.Combine(configPath, "settings.json");
            if (!_fileService.FileExists(settingsFile))
            {
                var defaultSettings = """
                    {
                      "permissions": {},
                      "env": {}
                    }
                    """;
                await _fileService.WriteAllTextAsync(settingsFile, defaultSettings, FileMode.Create, FileShare.None, cancellationToken);
            }

            // If settings.local.json still doesn't exist, create an empty one
            var localSettingsFile = Path.Combine(configPath, "settings.local.json");
            if (!_fileService.FileExists(localSettingsFile))
            {
                await _fileService.WriteAllTextAsync(localSettingsFile, "{}", FileMode.Create, FileShare.None, cancellationToken);
            }

            // If CLAUDE.md still doesn't exist, create empty one
            var claudeMdFile = Path.Combine(configPath, "CLAUDE.md");
            if (!_fileService.FileExists(claudeMdFile))
            {
                await _fileService.WriteAllTextAsync(claudeMdFile, "# Custom Instructions for Claude Code\n", FileMode.Create, FileShare.None, cancellationToken);
            }

            await Task.CompletedTask;
        }

        public async Task<ClaudeSettingsDto> GetSettings(string envName, CancellationToken cancellationToken)
        {
            var configPath = GetSettingsFilePath(envName);
            var dto = new ClaudeSettingsDto();

            if (!_fileService.FileExists(configPath))
            {
                return dto;
            }

            var content = await _fileService.ReadAllTextAsync(configPath, cancellationToken);

            try
            {
                var json = JsonNode.Parse(content);
                if (json == null) return dto;

                // effortLevel, model, and fastMode are the keys VibeRails reads. Permission
                // posture is YOLO-or-nothing and lives in CustomArgs (launch flags), so we
                // never read or touch Claude's permissions block here.
                dto.Effort = NormalizeSettingsEffort(TryReadString(json, "effortLevel"));
                dto.Model = TryReadString(json, "model") ?? "";
                dto.FastMode = TryReadBool(json, "fastMode");
            }
            catch (JsonException)
            {
                // Return defaults if JSON parsing fails
            }

            return dto;
        }

        private static string? TryReadString(JsonNode? root, string key)
        {
            if (root is null) return null;
            var node = root[key];
            if (node is null) return null;
            try { return node.GetValue<string>(); }
            catch { return null; }
        }

        private static bool TryReadBool(JsonNode? root, string key)
        {
            if (root is null) return false;
            var node = root[key];
            if (node is null) return false;
            try { return node.GetValue<bool>(); }
            catch { return false; }
        }

        private static string NormalizeSettingsEffort(string? value)
        {
            return value?.Trim().ToLowerInvariant() switch
            {
                "low" => "low",
                "medium" => "medium",
                "high" => "high",
                "xhigh" => "xhigh",
                "max" => "max",
                _ => ""
            };
        }

        public async Task SaveSettings(string envName, ClaudeSettingsDto settings, CancellationToken cancellationToken)
        {
            var configPath = GetSettingsFilePath(envName);

            // Ensure directory exists
            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory))
            {
                EnsureDirectoryExists(directory);
            }

            // Read existing content to preserve other fields
            JsonNode? json = null;
            if (_fileService.FileExists(configPath))
            {
                var content = await _fileService.ReadAllTextAsync(configPath, cancellationToken);
                try
                {
                    json = JsonNode.Parse(content);
                }
                catch (JsonException)
                {
                    // Start fresh if JSON is invalid
                }
            }

            json ??= new JsonObject();
            var obj = json.AsObject();

            // effortLevel, model, and fastMode are the keys VibeRails writes. Permission
            // posture is YOLO-or-nothing via CustomArgs launch flags, so we deliberately leave
            // the user's permissions block (allow / deny / defaultMode) untouched and only
            // strip stale top-level keys older VibeRails builds used to write.
            SetString(obj, "effortLevel", NormalizeSettingsEffort(settings.Effort));
            SetString(obj, "model", settings.Model);
            SetBool(obj, "fastMode", settings.FastMode);

            obj.Remove("effort");
            obj.Remove("noSessionPersistence");
            obj.Remove("permissionMode");
            obj.Remove("systemPrompt");
            obj.Remove("allowDangerouslySkipPermissions");
            obj.Remove("dangerouslyLoadDevelopmentChannels");
            obj.Remove("dangerouslySkipPermissions");
            obj.Remove("allowedTools");
            obj.Remove("appendSystemPrompt");
            obj.Remove("bare");
            obj.Remove("betas");
            obj.Remove("channels");
            obj.Remove("debug");
            obj.Remove("debugFilter");
            obj.Remove("skipPermissions");

            var options = new JsonSerializerOptions { WriteIndented = true };
            var jsonContent = json.ToJsonString(options);

            await _fileService.WriteAllTextAsync(configPath, jsonContent, FileMode.Create, FileShare.None, cancellationToken);
        }

        private static void SetString(JsonObject json, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                json[key] = value;
            else
                json.Remove(key);
        }

        private static void SetBool(JsonObject json, string key, bool value)
        {
            if (value)
                json[key] = true;
            else
                json.Remove(key);
        }

        private string GetSettingsFilePath(string envName)
        {
            var envBasePath = ParserConfigs.GetEnvPath();
            return Path.Combine(envBasePath, envName, GetConfigSubdirectory(), "settings.json");
        }
    }
}
