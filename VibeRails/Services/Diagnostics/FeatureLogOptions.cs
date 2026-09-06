using VibeRails.Utils;

namespace VibeRails.Services.Diagnostics;

/// <summary>Bounds the logger's memory, disk footprint, and on-demand read work.</summary>
public sealed class FeatureLogOptions
{
    /// <summary>
    /// Explicit journal directory. When null, the directory is resolved beside the state database
    /// the first time the store touches disk, so the singleton may be constructed before
    /// <c>GlobalRuntimePaths.Initialize</c> has run without silently falling back to a default path.
    /// </summary>
    public string? DirectoryPath { get; init; }
    public int QueueCapacity { get; init; } = 1024;
    public int MaxSegmentBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxRetainedFiles { get; init; } = 8;
    public int MaxReadEntries { get; init; } = 10_000;
    public TimeSpan ReadCacheDuration { get; init; } = TimeSpan.FromSeconds(2);

    internal string ResolveDirectoryPath()
    {
        if (DirectoryPath is not null)
            return DirectoryPath;
        var stateDirectory = Path.GetDirectoryName(ParserConfigs.GetStatePath());
        if (string.IsNullOrWhiteSpace(stateDirectory))
        {
            throw new InvalidOperationException(
                "The feature log directory cannot be resolved before the runtime paths are initialized.");
        }
        return Path.Combine(stateDirectory, "logs", "features");
    }
}
