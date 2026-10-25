using System.Collections.Concurrent;
using System.Text.Json;
using Serilog;
using VibeRails.Interfaces;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Services.VCA.Hooks;
using VibeRails.Utils;

namespace VibeRails.Services.ReviewRequests;

/// <summary>Opens the review approval popup for one request and reports how it ended.</summary>
public interface IReviewSignaturePopupLauncher
{
    /// <summary>True when this machine can open the popup at all: Windows, an interactive desktop, not CI.</summary>
    bool Available { get; }

    /// <summary>Runs the popup to completion. Null when it could not be opened; otherwise its exit code (0 means the approval was recorded).</summary>
    Task<int?> LaunchAsync(string reference, string repositoryPath, CancellationToken ct);
}

/// <summary>
/// The production launcher: the root backend re-launches its own executable with
/// <c>--vca-hook sign-review</c> in a new console window through <see cref="VcaHookConsoleRespawn"/>,
/// the same CREATE_NEW_CONSOLE route the commit popup uses, and waits for it. Only the reference
/// and the repository path travel in argv; the popup fetches the request itself.
/// </summary>
public sealed class ConsoleReviewSignaturePopupLauncher : IReviewSignaturePopupLauncher
{
    public bool Available =>
        OperatingSystem.IsWindows() && Environment.UserInteractive && VcaHookConsoleRespawn.AutomatedEnvironmentVariable() is null;

    /// <summary><c>TryRespawnAsync</c> swaps <c>--console-window</c> for <c>--console-window-attached</c> in the child.</summary>
    public static IReadOnlyList<string> Arguments(string reference, string repositoryPath) =>
        ["--vca-hook", "sign-review", "--review", reference, "--workdir", repositoryPath, "--console-window"];

    public Task<int?> LaunchAsync(string reference, string repositoryPath, CancellationToken ct) =>
        VcaHookConsoleRespawn.TryRespawnAsync(Arguments(reference, repositoryPath), TextWriter.Null, TextWriter.Null, ct);
}

/// <summary>
/// "Reviews requested of you" (VB-50 slice D): the requests viberails.ai holds for this account's verified
/// email, fetched on the Board sync cadence and kept beside state.db so every root backend shows the
/// same list; a rejection posted straight to viberails.ai; an approval that only ever happens in the
/// signing popup this service opens and watches. Nothing here signs, reads a key or sees a password.
/// </summary>
public sealed class ReviewInboxService(
    Func<ReviewRequestTransport> transports,
    ReviewInboxState state,
    Func<string?> apiKey,
    Func<string?> accountEmail,
    IAppEventBus events,
    IReviewSignaturePopupLauncher popups,
    TimeProvider? clock = null)
{
    /// <summary>Bounds one fetch.</summary>
    public static readonly TimeSpan FetchDeadline = TimeSpan.FromMinutes(1);

    /// <summary>"Check now" is a person's click; two clicks within this spacing are one check.</summary>
    public static readonly TimeSpan ManualRefreshSpacing = TimeSpan.FromSeconds(10);

    public const int MaxCommentChars = ReviewRequestService.MaxCommentChars;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, ReviewInboxSigning> _signing = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _fetchGate = new(1, 1);

    private DateTime Now => _clock.GetUtcNow().UtcDateTime;

    /// <summary>The scheduler's entry: fetches when the persisted due time has passed, never otherwise.</summary>
    public async Task RefreshDueAsync(CancellationToken ct)
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key)) return;
        var account = SessionSharingService.KeyFingerprint(key);
        if (!state.TryBegin(account, Now)) return;
        await FetchAsync(key, account, ct);
    }

    /// <summary>The page's "Check now": fetches at once (a repeat within <see cref="ManualRefreshSpacing"/> only re-reads the cache).</summary>
    public async Task<ReviewInboxView> RefreshNowAsync(string project, CancellationToken ct, IReadOnlyList<string>? openRoots = null)
    {
        var key = apiKey();
        if (!string.IsNullOrWhiteSpace(key))
        {
            var account = SessionSharingService.KeyFingerprint(key);
            if (state.TryBeginManual(account, Now, ManualRefreshSpacing))
                await FetchAsync(key, account, ct);
        }
        return View(project, openRoots);
    }

    /// <summary>
    /// The cached list for the current key's account, with this root's signing states and the clones
    /// Approve may rebuild in (<paramref name="openRoots"/>: the projects other open roots have).
    /// </summary>
    public ReviewInboxView View(string project, IReadOnlyList<string>? openRoots = null)
    {
        var key = apiKey();
        var hasKey = !string.IsNullOrWhiteSpace(key);
        var snapshot = state.Snapshot(hasKey ? SessionSharingService.KeyFingerprint(key!) : null);
        var items = snapshot.Requests
            .Select(item => new ReviewInboxItem(item.Id, item.Repository, item.RequestedBy, item.Status, item.FileCount, item.SessionCount,
                item.CardKey, item.CreatedUtc, item.ExpiresUtc, item.Decided, ReadUrl(item.Id), _signing.GetValueOrDefault(item.Id)))
            .ToList();
        return new ReviewInboxView(
            hasKey,
            hasKey ? accountEmail() : null,
            snapshot.EmailVerified,
            snapshot.FetchedUtc,
            snapshot.FetchedUtc is null && snapshot.LastErrorCode is null ? null : snapshot.NextDueUtc,
            snapshot.LastErrorCode,
            snapshot.LastError,
            items,
            snapshot.More,
            project,
            popups.Available,
            Clones(project, openRoots));
    }

    /// <summary>This root's project first, then each other open root's project once, by folder name.</summary>
    public static IReadOnlyList<ReviewInboxClone> Clones(string project, IReadOnlyList<string>? openRoots)
    {
        var others = new List<ReviewInboxClone>();
        foreach (var root in openRoots ?? [])
        {
            if (string.IsNullOrWhiteSpace(root) || ProjectPathComparer.Matches(root, project)
                || others.Any(clone => ProjectPathComparer.Matches(clone.Path, root))) continue;
            others.Add(new ReviewInboxClone(root, FolderName(root), false));
        }
        others.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name) is var byName and not 0
            ? byName : StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return [new ReviewInboxClone(project, FolderName(project), true), .. others];
    }

    private static string FolderName(string path) => Path.GetFileName(Path.TrimEndingDirectorySeparator(path));

    /// <summary>Where the reviewer reads the request: the hosted page, which also replays the attached sessions.</summary>
    public static string ReadUrl(string reference) => ReviewRequestTransport.Destination + "/Reviews/requests/" + reference;

    /// <summary>Posts a rejection with its comment to viberails.ai. No popup: nothing is signed.</summary>
    public async Task<ReviewInboxActionResult> RejectAsync(string reference, ReviewInboxRejectRequest? body, CancellationToken ct)
    {
        RequireReference(reference);
        var comment = body?.Comment?.Trim();
        if (string.IsNullOrEmpty(comment))
            throw new ReviewRequestException("invalid_request", "Say why in a comment when you reject a review.", StatusCodes.Status400BadRequest);
        if (comment.Length > MaxCommentChars || comment.Contains('\0'))
            throw new ReviewRequestException("invalid_request", $"A comment can hold up to {MaxCommentChars} characters.", StatusCodes.Status400BadRequest);
        var key = RequireKey();
        await transports().DecideAsync(key, reference, new ReviewInboxDecisionBody(ReviewDecisionKind.Rejected, null, comment), ct);
        state.MarkDecided(SessionSharingService.KeyFingerprint(key), reference, ReviewRequestStatus.Rejected);
        Publish();
        return new ReviewInboxActionResult(reference, ReviewRequestStatus.Rejected,
            "Your rejection was recorded on viberails.ai. The requester's VibeRails collects it on its next check.");
    }

    /// <summary>
    /// Opens the signing popup for one request and returns at once; the popup's outcome lands in the
    /// list's signing state. A popup that is still open for the same request is not opened twice.
    /// <paramref name="chosenPath"/> picks one of <see cref="Clones"/> to rebuild in (none means this
    /// root's project); a folder no open root has is refused, so the page cannot name an arbitrary path.
    /// </summary>
    public ReviewInboxActionResult BeginApprove(string reference, string project, string? chosenPath = null,
        IReadOnlyList<string>? openRoots = null)
    {
        RequireReference(reference);
        var repositoryPath = string.IsNullOrWhiteSpace(chosenPath) ? project
            : Clones(project, openRoots).FirstOrDefault(clone => ProjectPathComparer.Matches(clone.Path, chosenPath))?.Path
              ?? throw new ReviewRequestException("invalid_request",
                  "That folder is not open in VibeRails. Choose one of the listed clones, or open the clone in VibeRails first.",
                  StatusCodes.Status400BadRequest);
        var key = RequireKey();
        if (!popups.Available)
        {
            throw new ReviewRequestException("popup_unavailable",
                "The signing popup opens only on Windows with an interactive desktop, and not in CI. Approve this request from such a machine.",
                StatusCodes.Status409Conflict);
        }
        var started = new ReviewInboxSigning(reference, Now, null, null, "signing");
        while (!_signing.TryAdd(reference, started))
        {
            var existing = _signing[reference];
            if (existing.FinishedUtc is null)
            {
                throw new ReviewRequestException("signing_in_progress",
                    "The signing popup for this request is already open. Finish or close it first.", StatusCodes.Status409Conflict);
            }
            if (_signing.TryUpdate(reference, started, existing)) break;
        }
        _ = RunPopupAsync(reference, repositoryPath, SessionSharingService.KeyFingerprint(key));
        Publish();
        return new ReviewInboxActionResult(reference, "signing",
            "The signing popup opened in its own window. It shows the request, rebuilds the tree from your repository and then asks for your key password.",
            started);
    }

    private async Task RunPopupAsync(string reference, string repositoryPath, string account)
    {
        int? exit;
        try
        {
            exit = await popups.LaunchAsync(reference, repositoryPath, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // The launcher's own failure, never the popup's: the popup reports through its exit code.
            Log.Warning(ex, "[ReviewInbox] The signing popup for {Reference} could not be run", reference);
            exit = null;
        }
        var outcome = exit switch { 0 => "approved", null => "popup_unavailable", _ => "not_approved" };
        try
        {
            // The list entry first, then the signing outcome: a View between the two must never show an
            // approved popup on an entry that still reads open (VIBE-213, item 4).
            if (exit == 0) state.MarkDecided(account, reference, ReviewRequestStatus.Approved);
        }
        catch (Exception ex)
        {
            // Nothing awaits this task; the signing outcome below is still recorded, so only the bookkeeping is lost.
            Log.Warning(ex, "[ReviewInbox] Could not record the popup outcome for {Reference}", reference);
        }
        var started = _signing.TryGetValue(reference, out var current) ? current.StartedUtc : Now;
        _signing[reference] = new ReviewInboxSigning(reference, started, Now, exit, outcome);
        try
        {
            Publish();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[ReviewInbox] Could not publish the popup outcome for {Reference}", reference);
        }
    }

    private async Task FetchAsync(string key, string account, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(FetchDeadline);
        var token = deadline.Token;
        await _fetchGate.WaitAsync(token);
        try
        {
            ReviewInboxHosted hosted;
            try
            {
                hosted = await transports().InboxAsync(key, token);
            }
            catch (ReviewRequestTransportException ex)
            {
                Log.Information("[ReviewInbox] The list was not refreshed ({Code})", ex.Code);
                state.Fail(account, ex.Code, ex.Message);
                state.End(account, anyOpen: false, reachable: !ex.IsUnreachable, Now);
                Publish();
                return;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                state.Fail(account, "timeout", "viberails.ai did not answer in time. Check your connection and try again.");
                state.End(account, anyOpen: false, reachable: false, Now);
                Publish();
                return;
            }
            var anyOpen = hosted.Requests!.Any(item => item.Status == ReviewRequestStatus.Open && !item.Decided);
            state.Apply(account, hosted, Now);
            state.End(account, anyOpen, reachable: true, Now);
            Publish();
        }
        finally
        {
            _fetchGate.Release();
        }
    }

    private string RequireKey()
    {
        var key = apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new ReviewRequestException("no_api_key", "Sign in to viberails.ai in Settings before answering a review request.", StatusCodes.Status409Conflict);
        return key;
    }

    private static void RequireReference(string reference)
    {
        if (!ReviewRequestTransport.IsReference(reference))
            throw new ReviewRequestException("invalid_request", "That is not a review request reference.", StatusCodes.Status400BadRequest);
    }

    private void Publish() =>
        events.Publish(ReviewInboxEvents.Updated, new ReviewInboxChanged(Now), ReviewInboxJsonContext.Default.ReviewInboxChanged);
}

/// <summary>
/// The persisted inbox and its pacing, in <c>.review-inbox.json</c> beside state.db: the account
/// fingerprint, the next due time and backoff, and the last list as the host gave it. Shared by every
/// root backend on the machine, so a lease handoff or a second window neither fetches again at once
/// nor shows an empty list. A key change clears it: the list belongs to the account that fetched it.
/// </summary>
public sealed class ReviewInboxState
{
    public const string FileName = ".review-inbox.json";

    /// <summary>Cadence while a request addressed to this account is open and the host answers.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>Ceiling of the doubling backoff while nothing is open or the host is unreachable.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(2);

    private readonly Lock _gate = new();
    private readonly string? _path;
    private ReviewInboxFile _file = new();
    private DateTime _lastAttemptUtc;

    /// <summary>Process memory only; tests use this.</summary>
    public ReviewInboxState() : this(null) { }

    public ReviewInboxState(string? path)
    {
        _path = path;
    }

    public static ReviewInboxState BesideStateDatabase() =>
        new(CrossProcessFileLock.BesideStateDatabase(ParserConfigs.GetStatePath(), FileName));

    /// <summary>False until the next pass is due. Reloads the shared file so another root's pass counts.</summary>
    public bool TryBegin(string account, DateTime now)
    {
        lock (_gate)
        {
            Load();
            Account(account);
            if (now < _file.NextDueUtc) return false;
            _lastAttemptUtc = now;
            return true;
        }
    }

    /// <summary>A person's click: fetches unless one was attempted within <paramref name="spacing"/>.</summary>
    public bool TryBeginManual(string account, DateTime now, TimeSpan spacing)
    {
        lock (_gate)
        {
            Load();
            Account(account);
            if (_lastAttemptUtc != default && now - _lastAttemptUtc < spacing) return false;
            _lastAttemptUtc = now;
            return true;
        }
    }

    /// <summary>Stores a fetched list and clears the last error.</summary>
    public void Apply(string account, ReviewInboxHosted hosted, DateTime now)
    {
        lock (_gate)
        {
            Account(account);
            _file.FetchedUtc = now;
            _file.EmailVerified = hosted.EmailVerified;
            _file.Requests = [.. hosted.Requests ?? []];
            _file.More = hosted.More;
            _file.LastErrorCode = null;
            _file.LastError = null;
            Save();
        }
    }

    /// <summary>Records why the last fetch failed; the previous list stays.</summary>
    public void Fail(string account, string code, string message)
    {
        lock (_gate)
        {
            Account(account);
            _file.LastErrorCode = code;
            _file.LastError = message;
            Save();
        }
    }

    /// <summary>Schedules the next pass: the interval while something is open and the host answered, otherwise doubling to the ceiling.</summary>
    public void End(string account, bool anyOpen, bool reachable, DateTime now)
    {
        lock (_gate)
        {
            Account(account);
            var delay = anyOpen && reachable || _file.DelayTicks <= 0 ? Interval
                : TimeSpan.FromTicks(Math.Min(_file.DelayTicks * 2, MaxBackoff.Ticks));
            _file.DelayTicks = delay.Ticks;
            _file.NextDueUtc = now + delay;
            Save();
        }
    }

    /// <summary>A decision this machine made or saw confirmed: the entry shows it before the next fetch. An approval always wins, as on the host.</summary>
    public bool MarkDecided(string account, string reference, string status)
    {
        lock (_gate)
        {
            Load();
            if (_file.Account != account) return false;
            var index = _file.Requests.FindIndex(item => item.Id == reference);
            if (index < 0) return false;
            var item = _file.Requests[index];
            var next = item.Status == ReviewRequestStatus.Approved ? item.Status : status;
            _file.Requests[index] = item with { Status = next, Decided = true };
            Save();
            return true;
        }
    }

    /// <summary>The stored list for <paramref name="account"/>; an empty file for another account or none.</summary>
    public ReviewInboxFile Snapshot(string? account)
    {
        lock (_gate)
        {
            Load();
            if (account is null || _file.Account != account) return new ReviewInboxFile();
            return new ReviewInboxFile
            {
                Account = _file.Account,
                FetchedUtc = _file.FetchedUtc,
                NextDueUtc = _file.NextDueUtc,
                DelayTicks = _file.DelayTicks,
                EmailVerified = _file.EmailVerified,
                Requests = [.. _file.Requests],
                More = _file.More,
                LastErrorCode = _file.LastErrorCode,
                LastError = _file.LastError
            };
        }
    }

    /// <summary>The next due time as persisted, for tests and diagnostics.</summary>
    public DateTime NextDueUtc { get { lock (_gate) return _file.NextDueUtc; } }

    private void Account(string account)
    {
        if (_file.Account == account) return;
        _file = new ReviewInboxFile { Account = account };
        Save();
    }

    private void Load()
    {
        if (_path is null) return;
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize(File.ReadAllBytes(_path), ReviewInboxJsonContext.Default.ReviewInboxFile);
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
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_file, ReviewInboxJsonContext.Default.ReviewInboxFile));
            PrivateFilePermissions.EnsureFile(temp);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "[ReviewInbox] Could not persist the inbox");
        }
    }
}
