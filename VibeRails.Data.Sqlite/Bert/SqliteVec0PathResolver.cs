using System.Runtime.InteropServices;

namespace VibeRails.Services.BertV2;

internal static class SqliteVec0PathResolver
{
    public static string GetPath() => GetPath(AppContext.BaseDirectory,
        OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux,
        RuntimeInformation.ProcessArchitecture);

    internal static string GetPath(string baseDirectory, OSPlatform platform, Architecture architecture)
    {
        var rid = platform == OSPlatform.Windows
            ? "win-x64"
            : platform == OSPlatform.OSX
                ? (architecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64")
                : architecture == Architecture.Arm64
                    ? "linux-arm64"
                    : "linux-x64";

        var ext = platform == OSPlatform.Windows
            ? ".dll"
            : platform == OSPlatform.OSX
                ? ".dylib"
                : ".so";

        // NativeAOT publishes native assets beside the executable. Use an absolute
        // filename: dlopen on macOS/Linux does not search that directory by default.
        var directory = Path.GetFullPath(baseDirectory);
        var filename = "vec0" + ext;
        var candidate = Path.Combine(directory, filename);
        if (File.Exists(candidate))
            return candidate;

        // Framework-dependent development builds retain NuGet's runtime layout.
        candidate = Path.Combine(directory, "runtimes", rid, "native", filename);
        if (File.Exists(candidate))
            return candidate;

        return "vec0";
    }
}
