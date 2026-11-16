using Serilog;
using VibeRails.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.AgentTools;
using VibeRails.Utils;

namespace VibeRails.Services.Board;

/// <summary>
/// VIBE-338 (owner decision 2026-11-16: link automatically, and keep the check as a backstop). When the
/// post-commit hook runs in a VibeRails card terminal, git, the hook and <c>vb</c> all inherit the
/// terminal's <c>VIBERAILS_TOOL_CURRENT_SESSION_ID</c>. The commit is recorded against every card the
/// session is attached to, then linked to them with <c>link_board_commit</c>'s capture, snapshot and
/// fan-out. A card-scoped Code quality check names a recorded commit that is still not linked (the
/// link failed, or the commit was too large to capture).
/// </summary>
/// <remarks>
/// The commit already exists, so this is best effort and bounded: any failure is logged and never
/// reaches Git. Ordinary terminals, Git GUIs and CI have no session, so nothing is recorded or blamed.
/// The session's card resolves the way Git Guard's card exemptions do (CHECKS.md, VIBE-275): only
/// when the repository is that card's project or the session's recorded checkout.
/// </remarks>
public static class BoardSessionCommitLinker
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(15);

    public static async Task RecordAsync(string repository, string sha, CancellationToken cancellationToken)
    {
        var sessionId = Environment.GetEnvironmentVariable(LocalToolApiContext.CurrentSessionIdVariable)?.Trim();
        if (string.IsNullOrEmpty(sessionId)) return;
        var statePath = PathConstants.GetStateFilePath();
        if (!File.Exists(new SqliteStoragePaths(statePath).BoardDatabasePath)) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Budget);
        try
        {
            var linked = await RecordAsync(SqliteStorage.CreateBoardStore(statePath), new BoardCommitService(),
                repository, sessionId, sha, budget.Token);
            if (linked.Count > 0)
                Log.Information("[Board] Linked commit {Sha} to {Count} card(s) of session {Session}", sha, linked.Count, sessionId);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning("[Board] Gave up linking commit {Sha} after {Seconds} s; a card check will name it if it stays unlinked",
                sha, Budget.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "[Board] Could not record or link commit {Sha} for session {Session}", sha, sessionId);
        }
    }

    /// <summary>Records the commit, then links it. Returns the cards it was linked to.</summary>
    internal static async Task<IReadOnlyList<string>> RecordAsync(IBoardStore store, IBoardCommitService commits,
        string repository, string sessionId, string sha, CancellationToken cancellationToken)
    {
        if (await store.FindSessionLinkAsync(sessionId, cancellationToken) is not { } link) return [];
        var checkout = BoardPaths.NormalizeProjectPath(repository);
        if (!string.Equals(checkout, BoardPaths.NormalizeProjectPath(link.ProjectPath), BoardPaths.ProjectPathComparison)
            && !await store.IsSessionVisibleInProjectAsync(checkout, sessionId, cancellationToken))
            return [];
        var info = await commits.DescribeAsync(checkout, sha, cancellationToken);
        // Recorded before the slower capture, so a link that fails below is still named by the check.
        var recorded = await store.RecordSessionCommitAsync(link.ProjectPath, link.CardId, sessionId, info.Sha,
            checkout, info.Message, cancellationToken);
        if (recorded.Count == 0) return [];
        var diff = await commits.GetDiffAsync(checkout, info.Sha, cancellationToken);
        var snapshot = new SandboxDiffResponse(
            diff.Files.Select(f => new SandboxDiffFileResponse(f.FileName, f.Language, f.OriginalContent, f.ModifiedContent)).ToList(),
            diff.TotalChanges);
        var result = await store.AddCommitWithTargetsAsync(link.ProjectPath, link.CardId, info.Sha, info.Author, info.Message,
            info.CommittedUtc, snapshot, cancellationToken, sessionId);
        return result?.CardIds ?? [];
    }
}
