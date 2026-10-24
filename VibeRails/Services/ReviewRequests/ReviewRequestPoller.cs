using System.Text.Json;
using Serilog;
using VibeRails.DB;
using VibeRails.Interfaces;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;

namespace VibeRails.Services.ReviewRequests;

/// <summary>
/// Collects decisions for sent review requests (VB-50, slice C). Runs from the Board sync scheduler
/// and paces itself through <see cref="ReviewRequestPollState"/>: every 15 minutes while a sent
/// request is open, doubling to a two-hour ceiling while none is or the host is unreachable, never
/// on the scheduler's 60-second tick. Every decision lands through
/// <see cref="ReviewRequestService.RecordDecisionAsync(string, string, RecordReviewDecisionRequest, long?, CancellationToken)"/>,
/// so an approval is re-verified with viberails.ai and matched to a listed reviewer before the
/// ledger stores it; a rejection lands with its comment. Cancelled requests whose hosted copy has
/// not confirmed the cancellation are cancelled again here.
/// </summary>
public sealed class ReviewRequestPoller(
    IReviewRequestStore store,
    ReviewRequestService reviews,
    ReviewRequestTransport transport,
    IAppEventBus events,
    ReviewRequestPollState state,
    Func<string?> apiKey,
    TimeProvider? clock = null)
{
    /// <summary>Bounds one pass over every pending request.</summary>
    public static readonly TimeSpan PassDeadline = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long past the hosted expiry a request is still polled: an approval given just before the
    /// expiry stays collectable, and a desktop that was offline at the time still receives it.
    /// </summary>
    public static readonly TimeSpan ExpiryGrace = TimeSpan.FromDays(7);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task PollDueAsync(CancellationToken ct)
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key)) return;
        var account = SessionSharingService.KeyFingerprint(key);
        if (!state.TryBegin(account)) return;
        var now = _clock.GetUtcNow().UtcDateTime;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(PassDeadline);
        var token = deadline.Token;
        var reachable = true;
        var anyOpen = false;
        try
        {
            foreach (var request in await store.ListSentPendingAsync(token))
            {
                // Sent with another account's key: that account polls and cancels it, never this one.
                if (!string.Equals(request.SentKeyFingerprint, account, StringComparison.Ordinal)) continue;
                if (request.Status == ReviewRequestStatus.Cancelled)
                {
                    reachable &= await CancelRemoteAsync(key, request, token);
                    if (!reachable) break;
                    continue;
                }
                if (request.ExpiresUTC is { } expires && now > expires + ExpiryGrace) continue;
                var (ok, open) = await PollOneAsync(key, request, token);
                reachable &= ok;
                anyOpen |= open;
                if (!reachable) break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            reachable = false; // The pass deadline passed; the rest waits for the next due time.
        }
        finally
        {
            if (!ct.IsCancellationRequested) state.End(account, anyOpen, reachable);
        }
    }

    /// <summary>Returns whether the host was reachable and whether the request is still open afterwards.</summary>
    private async Task<(bool Reachable, bool Open)> PollOneAsync(string key, ReviewRequestRecord request, CancellationToken ct)
    {
        ReviewRequestHostedStatus remote;
        try { remote = await transport.PollAsync(key, request.RemoteId!, ct); }
        catch (ReviewRequestTransportException ex) when (ex.IsUnreachable)
        {
            Log.Information("[ReviewRequests] Decisions were not polled this pass ({Code})", ex.Code);
            return (false, true);
        }
        catch (ReviewRequestTransportException ex)
        {
            // A request the host no longer has stops counting toward the 15-minute cadence; it stays open here until the developer cancels it.
            Log.Information("[ReviewRequests] Request {Id} was not polled ({Code})", request.Id, ex.Code);
            return (true, ex.Code != "not_found");
        }

        var recorded = await store.GetRemoteDecisionIdsAsync(request.Id, ct);
        var status = request.Status;
        var changed = false;
        foreach (var decision in remote.Decisions!)
        {
            if (recorded.Contains(decision.Id) || state.IsRefused(request.Id, decision.Id)) continue;
            try
            {
                var summary = await reviews.RecordDecisionAsync(request.ProjectPath, request.Id,
                    new RecordReviewDecisionRequest(decision.Decision, decision.Trailer, decision.Comment, decision.Reviewer), decision.Id, ct);
                status = summary.Status;
                changed = true;
            }
            catch (ReviewRequestException ex) when (ex.Code == "verification_unavailable")
            {
                // The signature verifier is viberails.ai too; the approval is still pending and is retried next pass.
                Log.Information("[ReviewRequests] An approval on request {Id} could not be verified this pass", request.Id);
                if (changed) Publish(request, status);
                return (false, true);
            }
            catch (ReviewRequestException ex) when (ex.Code == "not_open")
            {
                break; // Cancelled meanwhile; the cancellation pass handles the hosted copy.
            }
            catch (ReviewRequestException ex)
            {
                // tree_mismatch, signature_rejected, not_a_reviewer, invalid_request: a hosted decision
                // this ledger cannot accept. Remembered for the process so it is not re-verified every pass.
                state.MarkRefused(request.Id, decision.Id);
                Log.Warning("[ReviewRequests] A hosted decision on request {Id} was refused locally ({Code}): {Message}", request.Id, ex.Code, ex.Message);
            }
        }
        if (changed) Publish(request, status);
        return (true, status == ReviewRequestStatus.Open);
    }

    private async Task<bool> CancelRemoteAsync(string key, ReviewRequestRecord request, CancellationToken ct)
    {
        try
        {
            await transport.CancelAsync(key, request.RemoteId!, ct);
            await store.MarkRemoteCancelledAsync(request.Id, _clock.GetUtcNow().UtcDateTime, ct);
            return true;
        }
        catch (ReviewRequestTransportException ex) when (ex.IsUnreachable)
        {
            return false;
        }
        catch (ReviewRequestTransportException ex)
        {
            Log.Information("[ReviewRequests] The hosted copy of request {Id} was not cancelled this pass ({Code})", request.Id, ex.Code);
            return true;
        }
    }

    private void Publish(ReviewRequestRecord request, string status) =>
        events.Publish(ReviewRequestEvents.Updated, new ReviewRequestChanged(request.Id, status, request.CardKey, true),
            ReviewRequestJsonContext.Default.ReviewRequestChanged);
}

/// <summary>
/// Paces the decision poll and remembers which hosted decisions this ledger refused. The next due
/// time and the backoff are persisted beside state.db with the account fingerprint only (never a
/// key, a reference or content), so a restart or a scheduler-lease handoff between root backends
/// does not poll again at once. Refusals live in process memory: a restart re-verifies them once.
/// </summary>
public sealed class ReviewRequestPollState
{
    public const string FileName = ".review-request-poll.json";
    /// <summary>Cadence while a sent request is open and the host answers.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    /// <summary>Ceiling of the doubling backoff while nothing is open or the host is unreachable.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(2);
    private readonly Lock _gate = new();
    private readonly string? _path;
    private readonly Func<DateTime> _clock;
    private readonly HashSet<(string RequestId, long DecisionId)> _refused = [];
    private ReviewRequestPollFile _file = new();

    /// <summary>Process memory only; tests use this.</summary>
    public ReviewRequestPollState() : this(null) { }
    public ReviewRequestPollState(string? path, Func<DateTime>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTime.UtcNow);
    }
    public static ReviewRequestPollState BesideStateDatabase() =>
        new(CrossProcessFileLock.BesideStateDatabase(ParserConfigs.GetStatePath(), FileName));

    /// <summary>False until the next pass is due. Reloads the shared file so another root's pass counts.</summary>
    public bool TryBegin(string account)
    {
        lock (_gate)
        {
            Load();
            Account(account);
            return _clock() >= _file.NextDueUtc;
        }
    }

    /// <summary>Schedules the next pass: the interval while something is open and the host answered, otherwise doubling to the ceiling.</summary>
    public void End(string account, bool anyOpen, bool reachable)
    {
        lock (_gate)
        {
            Account(account);
            var delay = anyOpen && reachable || _file.DelayTicks <= 0 ? Interval
                : TimeSpan.FromTicks(Math.Min(_file.DelayTicks * 2, MaxBackoff.Ticks));
            _file.DelayTicks = delay.Ticks;
            _file.NextDueUtc = _clock() + delay;
            Save();
        }
    }

    /// <summary>The next due time as persisted, for tests and diagnostics.</summary>
    public DateTime NextDueUtc { get { lock (_gate) return _file.NextDueUtc; } }

    public bool IsRefused(string requestId, long decisionId) { lock (_gate) return _refused.Contains((requestId, decisionId)); }
    public void MarkRefused(string requestId, long decisionId) { lock (_gate) _refused.Add((requestId, decisionId)); }

    private void Account(string account)
    {
        if (_file.Account == account) return;
        _file = new() { Account = account };
        _refused.Clear();
        Save();
    }

    private void Load()
    {
        if (_path is null) return;
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize(File.ReadAllBytes(_path), ReviewRequestJsonContext.Default.ReviewRequestPollFile);
            if (loaded is not null) _file = loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Keep the in-memory view; the worst case is one early pass.
        }
    }

    private void Save()
    {
        if (_path is null) return;
        try
        {
            var temp = _path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_file, ReviewRequestJsonContext.Default.ReviewRequestPollFile));
            PrivateFilePermissions.EnsureFile(temp);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "[ReviewRequests] Could not persist the poll state");
        }
    }
}
