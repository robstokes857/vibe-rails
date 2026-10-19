using VibeRails.Utils;

namespace VibeRails.Services.Board.Tracker;

/// <summary>
/// The OS file lock that lets one writing pull (or delivery drain) of one tracker run at a time
/// across every VibeRails process on this machine (browser app and VS Code can both be root
/// backends). Each tracker names its own file (<c>.jira-pull.lock</c>, <c>.ado-pull.lock</c>), so
/// a Jira pull never waits on an Azure DevOps one. Released by the OS if the process dies mid-pull.
/// </summary>
public class TrackerPullLock(string path)
{
    /// <summary>The lock file <paramref name="fileName"/> beside the state database.</summary>
    public static string PathBesideStateDatabase(string fileName) =>
        CrossProcessFileLock.BesideStateDatabase(ParserConfigs.GetStatePath(), fileName);

    internal CrossProcessFileLock? TryAcquire() => CrossProcessFileLock.TryAcquire(path);
}
