using System.Text.Json;
using System.Text.Json.Nodes;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Services.LlmClis
{
    public class ClaudeLlmCliEnvironment : BaseLlmCliEnvironment, IClaudeLlmCliEnvironment
    {
        public ClaudeLlmCliEnvironment(IDbService dbService, IFileService fileService) : base(dbService, fileService) { }

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

                dto.Model = json["model"]?.GetValue<string>() ?? "";
                dto.PermissionMode = json["permissionMode"]?.GetValue<string>() ?? "default";
                dto.AllowedTools = json["allowedTools"]?.GetValue<string>() ?? "";
                dto.DisallowedTools = json["disallowedTools"]?.GetValue<string>() ?? "";
                dto.SkipPermissions = json["skipPermissions"]?.GetValue<bool>() ?? false;
                dto.Verbose = json["verbose"]?.GetValue<bool>() ?? false;
            }
            catch (JsonException)
            {
                // Return defaults if JSON parsing fails
            }

            return dto;
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

            // Update or add our managed settings
            if (!string.IsNullOrEmpty(settings.Model))
                json["model"] = settings.Model;
            else
                json.AsObject().Remove("model");

            if (settings.PermissionMode != "default")
                json["permissionMode"] = settings.PermissionMode;
            else
                json.AsObject().Remove("permissionMode");

            if (!string.IsNullOrEmpty(settings.AllowedTools))
                json["allowedTools"] = settings.AllowedTools;
            else
                json.AsObject().Remove("allowedTools");

            if (!string.IsNullOrEmpty(settings.DisallowedTools))
                json["disallowedTools"] = settings.DisallowedTools;
            else
                json.AsObject().Remove("disallowedTools");

            if (settings.SkipPermissions)
                json["skipPermissions"] = true;
            else
                json.AsObject().Remove("skipPermissions");

            if (settings.Verbose)
                json["verbose"] = true;
            else
                json.AsObject().Remove("verbose");

            var options = new JsonSerializerOptions { WriteIndented = true };
            var jsonContent = json.ToJsonString(options);

            await _fileService.WriteAllTextAsync(configPath, jsonContent, FileMode.Create, FileShare.None, cancellationToken);
        }

        private string GetSettingsFilePath(string envName)
        {
            var envBasePath = ParserConfigs.GetEnvPath();
            return Path.Combine(envBasePath, envName, GetConfigSubdirectory(), "settings.json");
        }
    }
}
