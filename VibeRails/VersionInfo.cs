using System.Text.Json;

namespace VibeRails;

public static class VersionInfo
{
    private static readonly Lazy<string> _version = new Lazy<string>(LoadVersion);

    public static string Version => _version.Value;

    private static string LoadVersion()
    {
        try
        {
            // Try to read app_config.json from the same directory as the executable
            var exeDir = AppContext.BaseDirectory;
            var configPath = Path.Combine(exeDir, "app_config.json");

            if (!File.Exists(configPath))
            {
                Console.Error.WriteLine($"[VibeRails] Warning: Could not find app_config.json at '{configPath}', using fallback version");
                return "1.0.0"; // Fallback
            }

            var json = File.ReadAllText(configPath);

            // Simple JSON parsing for AOT compatibility
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("version", out var versionElement))
            {
                return versionElement.GetString() ?? "1.0.0";
            }

            return "1.0.0";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[VibeRails] Error loading version from app_config.json: {ex.Message}");
            return "1.0.0"; // Fallback
        }
    }
}
