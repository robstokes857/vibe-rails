using VibeRails.Utils;

namespace VibeRails.Services.Diagnostics;

/// <summary>Limits on-demand access to the existing Serilog files; no files are created or modified.</summary>
public sealed class DiagnosticLogOptions
{
    public string DirectoryPath { get; init; } = Path.Combine(PathConstants.GetInstallDirPath(), "logs");
    public int MaxFiles { get; init; } = 7;
    public int MaxBytesPerFile { get; init; } = 2 * 1024 * 1024;
    public int MaxReadEntries { get; init; } = 10_000;
    public int MaxMessageChars { get; init; } = 16 * 1024;
    public int MaxDirectoryEntries { get; init; } = 1024;
    public TimeSpan ReadCacheDuration { get; init; } = TimeSpan.FromSeconds(2);
}
