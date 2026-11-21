using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Services.Board.Sync;

/// <summary>
/// The Pro plan pause of remote Boards. The hosted service decides; these are the desktop's own
/// fixed words for its typed <c>pro_required</c> denial. The text is stored as a link's LastError,
/// so the paused reason is visible without a schema change and the status derives its
/// <c>pausedCode</c> and <c>pausedPayer</c> from it. The singleton also remembers, in memory only,
/// which boards an owner's plan refused, so the scheduler asks again about every 15 minutes
/// instead of every tick.
/// </summary>
public sealed class BoardSyncProPause(TimeProvider? time = null)
{
    public const string AccountMessage = "Remote Boards need Vibe Rails Pro. Upgrade to resume syncing; nothing has been deleted.";
    public const string BoardOwnerMessage = "This Board's owner needs Vibe Rails Pro. Syncing resumes when they renew; you do not need to upgrade.";
    public static readonly TimeSpan OwnerRecheck = TimeSpan.FromMinutes(15);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _ownerRecheck = [];

    /// <summary><c>account</c> or <c>board_owner</c> when the stored reason is one of the pause texts, else null.</summary>
    public static string? PayerOf(string? lastError) => lastError switch
    {
        AccountMessage => ProDenial.AccountPayer,
        BoardOwnerMessage => ProDenial.BoardOwnerPayer,
        _ => null
    };

    public static BoardSyncClientException Failure(ProDenial denial) =>
        new(denial.AccountPays ? AccountMessage : BoardOwnerMessage, ProDenial.Code, 403, feature: denial.Feature, payer: denial.Payer);

    /// <summary>True while this board's owner-plan denial is younger than <see cref="OwnerRecheck"/>.</summary>
    public bool IsOwnerPaused(string boardId)
    {
        lock (_gate) return _ownerRecheck.TryGetValue(boardId, out var next) && _time.GetUtcNow() < next;
    }

    public void PauseOwner(string boardId)
    {
        lock (_gate) _ownerRecheck[boardId] = _time.GetUtcNow() + OwnerRecheck;
    }

    public void Resume(string boardId)
    {
        lock (_gate) _ownerRecheck.Remove(boardId);
    }
}
