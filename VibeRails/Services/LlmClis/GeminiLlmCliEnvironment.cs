using System.Text.Json;
using System.Text.Json.Nodes;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Services.LlmClis
{
    public class GeminiLlmCliEnvironment : BaseLlmCliEnvironment, IGeminiLlmCliEnvironment
    {
        public GeminiLlmCliEnvironment(IDbService dbService, IFileService fileService)
            : base(dbService, fileService)
        {
        }

        public override string GetConfigSubdirectory() => "gemini";

        public override async Task CreateEnvironment(LLM_Environment environment, CancellationToken cancellationToken)
        {
            var geminiBasePath = Path.Combine(environment.Path, GetConfigSubdirectory());

            // Gemini uses XDG Base Directory specification
            // Create the XDG directory structure
            var xdgConfigPath = Path.Combine(geminiBasePath, "config", "gemini");
            var xdgDataPath = Path.Combine(geminiBasePath, "data", "gemini");
            var xdgCachePath = Path.Combine(geminiBasePath, "cache", "gemini");
            var xdgStatePath = Path.Combine(geminiBasePath, "state", "gemini");

            EnsureDirectoryExists(xdgConfigPath);
            EnsureDirectoryExists(xdgDataPath);
            EnsureDirectoryExists(xdgCachePath);
            EnsureDirectoryExists(xdgStatePath);

            // Copy entire configuration from default Gemini directory (~/.gemini)
            var defaultGeminiPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");
            CopyDirectoryRecursive(defaultGeminiPath, xdgConfigPath);

            // If settings.json still doesn't exist (no default Gemini config), create a basic one
            var settingsFile = Path.Combine(xdgConfigPath, "settings.json");
            if (!_fileService.FileExists(settingsFile))
            {
                var defaultSettings = """
                    {
                      "theme": "Default",
                      "selectedAuthType": "oauth-personal",
                      "checkForUpdates": true
                    }
                    """;
                await _fileService.WriteAllTextAsync(settingsFile, defaultSettings, FileMode.Create, FileShare.None, cancellationToken);
            }

            await Task.CompletedTask;
        }

        public async Task<GeminiSettingsDto> GetSettings(string envName, CancellationToken cancellationToken)
        {
            var settingsPath = GetSettingsFilePath(envName);
            var dto = new GeminiSettingsDto();

            if (!_fileService.FileExists(settingsPath))
            {
                return dto;
            }

            var json = await _fileService.ReadAllTextAsync(settingsPath, cancellationToken);
            var node = JsonNode.Parse(json);
            if (node == null) return dto;

            // Map Gemini CLI settings to our DTO
            dto.Theme = node["theme"]?.GetValue<string>() ?? "Default";
            dto.CheckForUpdates = node["checkForUpdates"]?.GetValue<bool>() ?? true;
            dto.VimMode = node["general"]?["vimMode"]?.GetValue<bool>() ?? false;
            dto.SandboxEnabled = node["sandbox"]?["enabled"]?.GetValue<bool>() ?? true;
            dto.AutoApproveTools = node["tools"]?["autoAccept"]?.GetValue<bool>() ?? false;
            dto.YoloMode = !(node["security"]?["disableYoloMode"]?.GetValue<bool>() ?? true);

            return dto;
        }

        public async Task SaveSettings(string envName, GeminiSettingsDto settings, CancellationToken cancellationToken)
        {
            var settingsPath = GetSettingsFilePath(envName);

            // Ensure directory exists
            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                EnsureDirectoryExists(directory);
            }

            // Read existing settings to preserve other fields
            JsonNode? node = null;
            if (_fileService.FileExists(settingsPath))
            {
                var existingJson = await _fileService.ReadAllTextAsync(settingsPath, cancellationToken);
                node = JsonNode.Parse(existingJson);
            }
            node ??= new JsonObject();

            // Update our managed settings
            node["theme"] = settings.Theme;
            node["checkForUpdates"] = settings.CheckForUpdates;

            // Nested settings - ensure parent objects exist
            node["general"] ??= new JsonObject();
            node["general"]!["vimMode"] = settings.VimMode;

            node["sandbox"] ??= new JsonObject();
            node["sandbox"]!["enabled"] = settings.SandboxEnabled;

            node["tools"] ??= new JsonObject();
            node["tools"]!["autoAccept"] = settings.AutoApproveTools;

            node["security"] ??= new JsonObject();
            node["security"]!["disableYoloMode"] = !settings.YoloMode;

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = node.ToJsonString(options);
            await _fileService.WriteAllTextAsync(settingsPath, json, FileMode.Create, FileShare.None, cancellationToken);
        }

        private string GetSettingsFilePath(string envName)
        {
            var envBasePath = Configs.GetEnvPath();
            return Path.Combine(envBasePath, envName, GetConfigSubdirectory(), "config", "gemini", "settings.json");
        }
    }
}
