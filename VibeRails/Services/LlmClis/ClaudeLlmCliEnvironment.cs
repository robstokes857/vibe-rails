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

                // Per-field readers are tolerant: a key whose value is the wrong JSON
                // type (e.g. allowedTools as Claude's native array form) just yields
                // the default for that one field instead of poisoning the whole DTO.
                dto.Effort = TryReadString(json, "effort") ?? "";
                dto.NoSessionPersistence = TryReadBool(json, "noSessionPersistence") ?? false;
                dto.PermissionMode = TryReadString(json, "permissionMode") ?? "default";
                dto.SystemPrompt = TryReadString(json, "systemPrompt") ?? "";
                dto.AllowDangerouslySkipPermissions = TryReadBool(json, "allowDangerouslySkipPermissions") ?? false;
                dto.DangerouslyLoadDevelopmentChannels = TryReadString(json, "dangerouslyLoadDevelopmentChannels") ?? "";
                dto.DangerouslySkipPermissions =
                    TryReadBool(json, "dangerouslySkipPermissions") ??
                    TryReadBool(json, "skipPermissions") ??
                    false;
                dto.AllowedTools = TryReadString(json, "allowedTools") ?? "";
                dto.AppendSystemPrompt = TryReadString(json, "appendSystemPrompt") ?? "";
                dto.Bare = TryReadBool(json, "bare") ?? false;
                dto.Betas = TryReadString(json, "betas") ?? "";
                dto.Channels = TryReadString(json, "channels") ?? "";
                dto.Debug = TryReadBool(json, "debug") ?? false;
                dto.DebugFilter = TryReadString(json, "debugFilter") ?? "";
            }
            catch (JsonException)
            {
                // Return defaults if JSON parsing fails
            }

            return dto;
        }

        private static string? TryReadString(JsonNode root, string key)
        {
            var node = root[key];
            if (node is null) return null;
            try { return node.GetValue<string>(); }
            catch { return null; }
        }

        private static bool? TryReadBool(JsonNode root, string key)
        {
            var node = root[key];
            if (node is null) return null;
            try { return node.GetValue<bool>(); }
            catch { return null; }
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

            // Update or add our managed settings
            SetString(obj, "effort", settings.Effort);
            SetBool(obj, "noSessionPersistence", settings.NoSessionPersistence);

            if (settings.PermissionMode != "default")
                obj["permissionMode"] = settings.PermissionMode;
            else
                obj.Remove("permissionMode");

            SetString(obj, "systemPrompt", settings.SystemPrompt);
            SetBool(obj, "allowDangerouslySkipPermissions", settings.AllowDangerouslySkipPermissions);
            SetString(obj, "dangerouslyLoadDevelopmentChannels", settings.DangerouslyLoadDevelopmentChannels);
            SetBool(obj, "dangerouslySkipPermissions", settings.DangerouslySkipPermissions);
            SetString(obj, "allowedTools", settings.AllowedTools);
            SetString(obj, "appendSystemPrompt", settings.AppendSystemPrompt);
            SetBool(obj, "bare", settings.Bare);
            SetString(obj, "betas", settings.Betas);
            SetString(obj, "channels", settings.Channels);
            SetBool(obj, "debug", settings.Debug);
            SetString(obj, "debugFilter", settings.DebugFilter);

            // skipPermissions is a legacy alias for dangerouslySkipPermissions: GetSettings
            // falls back to it when the new key is missing, so we must clear it whenever we
            // write the new key — otherwise the legacy value would shadow the user's choice.
            // Other previously-managed fields (model, disallowedTools, verbose) have no
            // VibeRails mapping anymore; leave them untouched so a user's manual edits stick.
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
