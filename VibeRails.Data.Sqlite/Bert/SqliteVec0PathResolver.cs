using System.Runtime.InteropServices;

namespace VibeRails.Services.BertV2;

internal static class SqliteVec0PathResolver
{
    public static string GetPath()
    {
        var rid = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "win-x64"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? (RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
                : RuntimeInformation.OSArchitecture == Architecture.Arm64
                    ? "linux-arm64"
                    : "linux-x64";

        var ext = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ".dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? ".dylib"
                : ".so";

        var candidate = Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", "vec0");
        if (File.Exists(candidate + ext))
            return candidate;

        return "vec0";
    }
}
