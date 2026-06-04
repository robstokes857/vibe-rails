using System.Text.Json;
using System.Text.Json.Nodes;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails.Services.LlmClis
{
    public class GeminiLlmCliEnvironment : BaseLlmCliEnvironment, IGeminiLlmCliEnvironment
    {
        public GeminiLlmCliEnvironment(IFileService fileService)
            : base(fileService)
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
                      "general": {
                        "enableAutoUpdate": true
                      },
                      "tools": {
                        "sandbox": true
                      }
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

            // Permission posture (defaultApprovalMode) is YOLO-or-nothing via CustomArgs,
            // so VibeRails neither reads nor edits it here.
            dto.Theme = node["theme"]?.GetValue<string>() ?? "Default";
            dto.CheckForUpdates = node["general"]?["enableAutoUpdate"]?.GetValue<bool>() ?? true;
            dto.VimMode = node["general"]?["vimMode"]?.GetValue<bool>() ?? false;
            dto.SandboxEnabled = node["tools"]?["sandbox"]?.GetValue<bool>() ?? true;

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

            // Nested settings - ensure parent objects exist. defaultApprovalMode is NOT
            // written here: permission posture is YOLO-or-nothing via CustomArgs, so any
            // user-set approval mode is left untouched.
            node["general"] ??= new JsonObject();
            node["general"]!["vimMode"] = settings.VimMode;
            node["general"]!["enableAutoUpdate"] = settings.CheckForUpdates;

            node["tools"] ??= new JsonObject();
            node["tools"]!["sandbox"] = settings.SandboxEnabled;

            var root = node.AsObject();
            root.Remove("checkForUpdates");
            root.Remove("sandbox");
            if (node["tools"] is JsonObject tools)
                tools.Remove("autoAccept");

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = node.ToJsonString(options);
            await _fileService.WriteAllTextAsync(settingsPath, json, FileMode.Create, FileShare.None, cancellationToken);
        }

        private string GetSettingsFilePath(string envName)
        {
            var envBasePath = ParserConfigs.GetEnvPath();
            return Path.Combine(envBasePath, envName, GetConfigSubdirectory(), "config", "gemini", "settings.json");
        }
    }
}
