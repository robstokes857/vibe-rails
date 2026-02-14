using Microsoft.Extensions.Configuration;

namespace VibeRails;

public static class VersionInfo
{
    private static string? _version;

    public static string Version => _version ?? "1.0.0";

    public static void Initialize(IConfiguration configuration)
    {
        _version = configuration["VibeRails:Version"] ?? "1.0.0";
    }
}
