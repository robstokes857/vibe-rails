namespace VibeRails.Services;

public enum GitHookFileState
{
    Missing,
    Current,
    Stale,
    Disabled
}

public sealed record GitHookFileStatus(
    string Name,
    string Path,
    GitHookFileState State,
    bool HasVibeRailsSection,
    string Message)
{
    public bool IsCurrent => State == GitHookFileState.Current;
}

public sealed record GitHooksStatus(
    string RepositoryPath,
    string HooksPath,
    GitHookFileStatus PreCommit,
    GitHookFileStatus CommitMessage)
{
    public bool IsInstalled => PreCommit.IsCurrent && CommitMessage.IsCurrent;

    public bool NeedsRepair =>
        !IsInstalled &&
        (PreCommit.HasVibeRailsSection || CommitMessage.HasVibeRailsSection);

    public string State => IsInstalled
        ? "active"
        : NeedsRepair
            ? "repair"
            : "missing";
}
