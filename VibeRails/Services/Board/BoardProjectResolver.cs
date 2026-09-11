using VibeRails.Services.AgentTools;
using VibeRails.Services.Git;
using VibeRails.Utils;

namespace VibeRails.Services.Board;

/// <summary>
/// Which project's board a caller means. The board never trusts a project path from a request;
/// it is derived from where the process is running.
/// </summary>
public interface IBoardProjectResolver
{
    /// <summary>
    /// Resolution order: the dashboard's root path when this process has one (root backend) →
    /// the card this terminal session was launched for (<c>VIBERAILS_TOOL_CURRENT_SESSION_ID</c>,
    /// which also covers environments running in a sandbox/worktree clone whose cwd is not the
    /// project) → the git root above the working directory → the working directory itself.
    /// </summary>
    Task<string> ResolveAsync(CancellationToken cancellationToken = default);

    /// <summary>The checkout to capture commits from now, independent of the board's source project.</summary>
    string GitWorkingDirectory { get; }

    /// <summary>The terminal session this process was launched from, when VibeRails spawned it.</summary>
    string? CurrentSessionId { get; }
}

public sealed class BoardProjectResolver(IBoardStore store) : IBoardProjectResolver
{
    public string? CurrentSessionId => EmptyToNull(Environment.GetEnvironmentVariable(LocalToolApiContext.CurrentSessionIdVariable));

    public string GitWorkingDirectory
    {
        get
        {
            var rootPath = ParserConfigs.GetRootPath();
            var cwd = Directory.GetCurrentDirectory();
            return BoardStore.NormalizeProjectPath(!string.IsNullOrWhiteSpace(rootPath)
                ? rootPath : GitCli.FindRoot(cwd) ?? cwd);
        }
    }

    public async Task<string> ResolveAsync(CancellationToken cancellationToken = default)
    {
        var rootPath = ParserConfigs.GetRootPath();
        if (!string.IsNullOrWhiteSpace(rootPath))
            return BoardStore.NormalizeProjectPath(rootPath);

        if (CurrentSessionId is { } sessionId)
        {
            var link = await store.FindSessionLinkAsync(sessionId, cancellationToken);
            if (link is not null)
                return link.ProjectPath;
        }

        var cwd = Directory.GetCurrentDirectory();
        return BoardStore.NormalizeProjectPath(GitCli.FindRoot(cwd) ?? cwd);
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// Which linked sessions currently have a live terminal tab. The root backend answers from its
/// in-memory tab host; hosts without one (stdio MCP) report nothing live.
/// </summary>
public interface IBoardLiveSessionProbe
{
    /// <summary>Session id → tab id for every tab with an active session.</summary>
    Task<IReadOnlyDictionary<string, string>> GetLiveSessionsAsync(CancellationToken cancellationToken = default);
}

public sealed class NullBoardLiveSessionProbe : IBoardLiveSessionProbe
{
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    public Task<IReadOnlyDictionary<string, string>> GetLiveSessionsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Empty);
}
