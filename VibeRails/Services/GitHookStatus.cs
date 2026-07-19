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
    GitHookFileStatus CommitMessage,
    GitHookFileStatus PostCommit)
{
    public bool IsInstalled => PreCommit.IsCurrent && CommitMessage.IsCurrent && PostCommit.IsCurrent;

    public bool NeedsRepair =>
        !IsInstalled &&
        (PreCommit.HasVibeRailsSection || CommitMessage.HasVibeRailsSection || PostCommit.HasVibeRailsSection);

    public string State => IsInstalled
        ? "active"
        : NeedsRepair
            ? "repair"
            : "missing";
}
