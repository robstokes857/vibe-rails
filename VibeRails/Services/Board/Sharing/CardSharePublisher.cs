using System.Text.Json;
using Serilog;
using Serilog.Events;
using VibeRails.DB;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;

namespace VibeRails.Services.Board.Sharing;

/// <summary>Publishes only explicitly selected saved cards and refreshes existing, creator-key-bound publications.</summary>
public sealed class CardSharePublisher(IBoardStore boards, CardShareCapture capture, CardShareClient client,
    ISessionArchiveReader archives, BoardSyncLock syncLock, CardShareRefreshState refreshState)
{
    /// <summary>Bounds one discovery sweep, including every capture and transfer it performs.</summary>
    public static readonly TimeSpan SweepDeadline = TimeSpan.FromMinutes(2);
    /// <summary>How long a capture waits for a running Board sync; a sync is a handful of small requests.</summary>
    internal static readonly TimeSpan LockWait = TimeSpan.FromSeconds(10);

    public Task<CardShareResult> ListAsync(string project, string identity, int? before, CancellationToken ct) => RunAsync(async () =>
    {
        var card = await CardAsync(project, identity, ct);
        var key = Key();
        if (before is <= 0) throw new BoardValidationException("Invalid sharing-link cursor.");
        var links = await client.ListAsync(key, card.Key, card.Id, before, ct);
        RequireSameAccount(key);
        return new(true, "", Links: links, NextBefore: links.Count == 100 ? links[^1].Id : null);
    }, ct);

    public Task<CardShareResult> CreateAsync(string project, string identity, string? displayName, CancellationToken ct)
        => CreateAsync(project, identity, displayName, null, null, ct);

    /// <summary>Access is "public" (default) or "email" with the people who may open the link.</summary>
    public Task<CardShareResult> CreateAsync(string project, string identity, string? displayName, string? access, IReadOnlyList<string>? emails, CancellationToken ct) => RunAsync(async () =>
    {
        var name = Name(displayName);
        var (mode, addresses) = Audience(access, emails);
        var key = Key();
        var card = await CardAsync(project, identity, ct);
        // A server without the sharing update ignores the audience and would publish the card to
        // anyone. Ask it first, before anything is captured or sent, so it publishes nothing.
        if (mode == ShareAudience.Email && !await SupportsListedPeopleAsync(key, ct))
            throw new BoardValidationException("The sharing server does not support links for listed people yet, so no link was created. Ask the server administrator to deploy the sharing update, or share with anyone who has the link.");
        var document = await CaptureAsync(project, card.Id, ct);
        RequireSameAccount(key);
        var result = await client.PublishAsync(key, name, card.Id, document, mode, mode == ShareAudience.Email ? addresses : null, ct);
        if (!ShareAudience.Confirms(mode, result.Link.Access) || (mode == ShareAudience.Email && !ShareAudience.SamePeople(addresses, result.Link.Recipients)))
        {
            // The server confirmed support above but did not apply the audience, so this link is
            // not what was asked for. Take it back before anyone receives it; nothing is queued for it.
            await client.RevokeAsync(key, result.Link.Id, ct);
            throw new BoardValidationException("The sharing server did not confirm the listed people for this link; the link it created was revoked. Contact the server administrator.");
        }
        // Publication may already exist even if queuing fails or the client closes. The scheduler
        // rediscovers it from this exact key; never retry this creation POST automatically.
        var queued = await QueueAsync(key, result.UploadSessions, ct);
        // The new publication holds exactly this snapshot, so the next sweep must not resend it.
        refreshState.Confirm(SessionSharingService.KeyFingerprint(key), card.Id, document.Hash, null);
        var changed = !string.Equals(key, ParserConfigs.GetApiKey(), StringComparison.Ordinal);
        return new(true, changed ? "Link created with the previous account. Switch back to that account to upload recordings and manage it."
            : !queued ? "Link created. Some recordings are unavailable locally; refresh after restoring them."
            : result.UploadSessions.Count > 0 ? "Link created. Recordings become playable after the sessions end and upload. Keep VibeRails open."
            : "Read-only card link created.", result.Link);
    }, ct, creation: true);

    public Task<CardShareResult> RenameAsync(string project, string identity, int id, string? displayName, CancellationToken ct) => RunAsync(async () =>
    {
        var name = Name(displayName); var key = Key();
        await OwnedLinkAsync(project, identity, key, id, ct);
        RequireSameAccount(key);
        await client.RenameAsync(key, id, name, ct);
        return new(true, "Link renamed.");
    }, ct);
    /// <summary>Replaces who may open one of this card's links. A revoked link stays revoked.</summary>
    public Task<CardShareResult> SetAccessAsync(string project, string identity, int id, string? access, IReadOnlyList<string>? emails, CancellationToken ct) => RunAsync(async () =>
    {
        var (mode, addresses) = Audience(access, emails); var key = Key();
        await OwnedLinkAsync(project, identity, key, id, ct);
        RequireSameAccount(key);
        await client.SetAccessAsync(key, id, mode, mode == ShareAudience.Email ? addresses : [], ct);
        return new(true, mode == ShareAudience.Email ? "Only the listed people can open this link now." : "Anyone with this link can open it now.");
    }, ct);
    public Task<CardShareResult> RevokeAsync(string project, string identity, int id, CancellationToken ct) => RunAsync(async () =>
    {
        var key = Key(); await OwnedLinkAsync(project, identity, key, id, ct);
        RequireSameAccount(key);
        await client.RevokeAsync(key, id, ct);
        return new(true, "Link revoked. Its card, documents and recordings are no longer public through this link.");
    }, ct);

    public Task<CardShareResult> RefreshAsync(string project, string identity, CancellationToken ct) => RunAsync(async () =>
    {
        var key = Key(); var card = await CardAsync(project, identity, ct);
        var document = await CaptureAsync(project, card.Id, ct);
        var account = SessionSharingService.KeyFingerprint(key);
        var count = 0; var after = 0; var allQueued = true;
        while (true)
        {
            RequireSameAccount(key);
            var sources = await client.SourcesAsync(key, after, ct);
            foreach (var source in sources.Where(s => s.SourceKey == card.Key && s.LocalCardId == card.Id))
            {
                RequireSameAccount(key);
                var result = await client.RefreshAsync(key, source, document, ct);
                allQueued &= await QueueAsync(key, result.UploadSessions, ct);
                refreshState.Confirm(account, card.Id, document.Hash, source.Id);
                count++;
            }
            if (sources.Count < 100) break;
            after = sources[^1].Id;
        }
        return new(count > 0, count == 0 ? "No active shares were created with this account key. Create a link or sign in with its original key."
            : allQueued ? "Shared card updated. Linked recordings become playable after they end and upload."
            : "Shared card updated, but some recordings are unavailable on this computer.");
    }, ct);

    /// <summary>
    /// One bounded discovery page per due sweep. Discovery runs every 15 minutes while this account
    /// has publications and backs off to two hours while it has none or the host is unreachable, so
    /// a signed-in desktop without shared cards is not polling every minute. Unchanged snapshots are
    /// never retransferred, and a missing local source never deletes a hosted copy.
    /// </summary>
    public async Task RefreshDueAsync(CancellationToken ct)
    {
        var key = ParserConfigs.GetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return;
        var account = SessionSharingService.KeyFingerprint(key);
        if (!refreshState.TryBeginSweep(account, out var cursor)) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(SweepDeadline);
        var token = deadline.Token;
        var found = false; var completed = false; var more = false;
        try
        {
            var sources = await client.SourcesAsync(key, cursor, token);
            found = sources.Count > 0;
            foreach (var source in sources)
            {
                RequireSameAccount(key);
                try { await RefreshSourceAsync(key, account, source, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    // One unavailable publication must not starve the rest. Domain and transport
                    // wording is fixed local text; runtime messages carry no key, link or content.
                    Log.Write(ex is BoardValidationException or CardShareTransportException ? LogEventLevel.Information : LogEventLevel.Warning,
                        ex, "[CardSharing] Publication {SourceId} was not refreshed this sweep", source.Id);
                }
                cursor = source.Id;
            }
            more = sources.Count >= 100;
            completed = true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            more = true; // The sweep deadline passed; continue from the cursor on the next tick.
        }
        finally
        {
            if (!ct.IsCancellationRequested) refreshState.EndSweep(account, more ? cursor : 0, completed && found, more);
        }
    }

    private async Task RefreshSourceAsync(string key, string account, CardShareSource source, CancellationToken ct)
    {
        // A remote source key is only a lookup. Its stored project owns every subsequent read.
        var local = await boards.FindLocalCardAsync(source.LocalCardId ?? source.SourceKey, ct);
        if (local is null || local.Key != source.SourceKey) return;
        var document = await TryCaptureAsync(local.ProjectPath, local.Id, ct);
        if (document is null) return; // A long sync holds the lock; this publication waits for the next sweep.
        if (source.Revision == document.Hash && refreshState.IsConfirmed(account, local.Id, document.Hash)) return;
        if (source.Revision != document.Hash && refreshState.WasSent(account, source.Id, document.Hash))
        {
            // The host computed a different revision for this exact content, so the two products
            // serialize differently. Resending would loop forever; the last complete publication stands.
            if (refreshState.MarkDiverged(source.Id))
                Log.Warning("[CardSharing] Publication {SourceId} reports a revision that differs from the desktop hash of unchanged content; update both applications", source.Id);
            return;
        }
        RequireSameAccount(key);
        var result = await client.RefreshAsync(key, source, document, ct);
        await QueueAsync(key, result.UploadSessions, ct);
        refreshState.Confirm(account, local.Id, document.Hash, source.Id);
    }

    /// <summary>
    /// Captures under the cross-process Board sync lock so sync never rewrites the card mid-read,
    /// and releases it before any transfer so other root backends keep syncing while bytes move.
    /// Waits briefly for a running sync instead of failing; the UpdatedUtc re-check guards the snapshot.
    /// </summary>
    private async Task<CardShareDocument> CaptureAsync(string project, string cardId, CancellationToken ct) =>
        await TryCaptureAsync(project, cardId, ct) ?? throw new BoardValidationException("A Board sync is running. Try again shortly.");
    private async Task<CardShareDocument?> TryCaptureAsync(string project, string cardId, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + LockWait;
        while (true)
        {
            if (syncLock.TryAcquire() is { } held)
            {
                using (held) return await capture.CaptureAsync(project, cardId, ct);
            }
            if (DateTime.UtcNow >= deadline) return null;
            await Task.Delay(250, ct);
        }
    }

    /// <summary>The capabilities question creates nothing, so its failures carry no "a link may exist" note.</summary>
    private async Task<bool> SupportsListedPeopleAsync(string key, CancellationToken ct)
    {
        try { return await client.SupportsListedPeopleAsync(key, ct); }
        catch (CardShareTransportException ex) { throw new BoardValidationException(ex.Message); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException || ex is OperationCanceledException && !ct.IsCancellationRequested)
        { throw new BoardValidationException("Could not reach the sharing server to confirm support for listed people. Check your connection and try again; no link was created."); }
    }

    private async Task<bool> QueueAsync(string key, IReadOnlyList<Guid> sessions, CancellationToken ct)
    {
        var complete = true; var fingerprint = SessionSharingService.KeyFingerprint(key);
        foreach (var id in sessions)
            complete &= await archives.EnsureSessionShareUploadAsync(id.ToString("D"), fingerprint, DateTime.UtcNow, ct);
        return complete;
    }
    private async Task<BoardCardRecord> CardAsync(string project, string identity, CancellationToken ct) =>
        await boards.FindCardAsync(project, identity, ct) ?? throw new BoardValidationException("Card not found in this project.");
    private async Task OwnedLinkAsync(string project, string identity, string key, int id, CancellationToken ct)
    {
        if (id <= 0 || id == int.MaxValue) throw new BoardValidationException("Invalid sharing-link ID.");
        var card = await CardAsync(project, identity, ct);
        var links = await client.ListAsync(key, card.Key, card.Id, id + 1, ct);
        if (!links.Any(l => l.Id == id)) throw new BoardValidationException("Sharing link not found on this card.");
    }
    private static string Key()
    {
        var key = ParserConfigs.GetApiKey();
        return string.IsNullOrWhiteSpace(key) ? throw new BoardValidationException("Sign in to your VibeRails account before sharing.") : key;
    }
    private static string Name(string? name) => name?.Trim() is { Length: >= 1 and <= 160 } value
        ? value : throw new BoardValidationException("Enter a link name of 1 to 160 characters.");
    private static (string Mode, IReadOnlyList<string> Addresses) Audience(string? access, IReadOnlyList<string>? emails)
        => ShareAudience.TryNormalize(access, emails, out var mode, out var addresses, out var error) ? (mode, addresses) : throw new BoardValidationException(error);
    private static void RequireSameAccount(string key)
    {
        if (!string.Equals(key, ParserConfigs.GetApiKey(), StringComparison.Ordinal))
            throw new BoardValidationException("The signed-in account changed. Refresh sharing links before continuing.");
    }
    private static async Task<CardShareResult> RunAsync(Func<Task<CardShareResult>> action, CancellationToken ct, bool creation = false)
    {
        try { return await action(); }
        catch (Exception ex) when (ex is BoardValidationException or CardShareTransportException)
        { return new(false, ex.Message + (creation && ex is CardShareTransportException ? Uncertain : "")); }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            return new(false, "Card sharing could not finish. Check your connection and refresh the links." + (creation ? Uncertain : ""));
        }
    }
    private const string Uncertain = " Check the link list before creating another; this request may already have created a link.";
}

/// <summary>
/// Paces hosted discovery and remembers which complete snapshot each publication already holds, so
/// unchanged cards are never retransferred. Persisted beside state.db with hashes and numbers only
/// (account fingerprint, local row IDs, publication numbers, next due time; never links, keys or
/// content), so a restart or a scheduler-lease handoff between root backends does not resend
/// every shared card. An unreadable file costs one reconciliation transfer per publication.
/// </summary>
public sealed class CardShareRefreshState
{
    public const string FileName = ".card-share-state.json";
    /// <summary>Discovery cadence while the account has publications.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    /// <summary>Ceiling of the doubling backoff while nothing is published or the host is unreachable.</summary>
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(2);
    private const int MaxEntries = 10000;
    private readonly Lock _gate = new();
    private readonly string? _path;
    private readonly Func<DateTime> _clock;
    private readonly HashSet<int> _diverged = [];
    private CardShareRefreshFile _file = new();

    /// <summary>Process memory only; tests and the headless round trip use this.</summary>
    public CardShareRefreshState() : this(null) { }
    public CardShareRefreshState(string? path, Func<DateTime>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTime.UtcNow);
    }
    public static CardShareRefreshState BesideStateDatabase() =>
        new(CrossProcessFileLock.BesideStateDatabase(ParserConfigs.GetStatePath(), FileName));

    /// <summary>False until the next discovery is due. Reloads shared state so another root's sweep counts.</summary>
    public bool TryBeginSweep(string account, out int cursor)
    {
        lock (_gate)
        {
            Load(); Account(account);
            cursor = _file.Cursor;
            return _clock() >= _file.NextDueUtc;
        }
    }
    /// <summary>Schedules the next sweep: right away while a page continues, the interval after a productive sweep, else doubling backoff.</summary>
    public void EndSweep(string account, int cursor, bool productive, bool more)
    {
        lock (_gate)
        {
            Account(account);
            _file.Cursor = cursor;
            var now = _clock();
            if (more) { _file.NextDueUtc = now; Save(); return; }
            var delay = productive || _file.DelayTicks <= 0 ? Interval
                : TimeSpan.FromTicks(Math.Min(_file.DelayTicks * 2, MaxBackoff.Ticks));
            _file.DelayTicks = delay.Ticks;
            _file.NextDueUtc = now + delay;
            Save();
        }
    }
    public bool IsConfirmed(string account, string localCardId, string hash)
    { lock (_gate) { Account(account); return _file.Confirmed.GetValueOrDefault(localCardId) == hash; } }
    public bool WasSent(string account, int sourceId, string hash)
    { lock (_gate) { Account(account); return _file.Sent.GetValueOrDefault(sourceId.ToString()) == hash; } }
    /// <summary>True the first time a publication is reported divergent in this process, so it is logged once.</summary>
    public bool MarkDiverged(int sourceId) { lock (_gate) return _diverged.Add(sourceId); }
    /// <summary>Records that the host holds <paramref name="hash"/> for this card, and for one publication when known.</summary>
    public void Confirm(string account, string localCardId, string hash, int? sourceId)
    {
        lock (_gate)
        {
            Load(); Account(account);
            if (_file.Confirmed.Count >= MaxEntries) _file.Confirmed.Clear();
            if (_file.Sent.Count >= MaxEntries) _file.Sent.Clear();
            _file.Confirmed[localCardId] = hash;
            if (sourceId is { } id) _file.Sent[id.ToString()] = hash;
            Save();
        }
    }

    private void Account(string account)
    {
        if (_file.Account == account) return;
        _file = new() { Account = account };
        _diverged.Clear();
        Save();
    }
    private void Load()
    {
        if (_path is null) return;
        try
        {
            if (!File.Exists(_path)) return;
            var loaded = JsonSerializer.Deserialize(File.ReadAllBytes(_path), CardSharingJsonContext.Default.CardShareRefreshFile);
            if (loaded is null) return;
            loaded.Confirmed ??= [];
            loaded.Sent ??= [];
            _file = loaded;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Keep the in-memory view; the worst case is one extra reconciliation transfer per publication.
        }
    }
    private void Save()
    {
        if (_path is null) return;
        try
        {
            var temp = _path + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(_file, CardSharingJsonContext.Default.CardShareRefreshFile));
            PrivateFilePermissions.EnsureFile(temp);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Debug(ex, "[CardSharing] Could not persist refresh state");
        }
    }
}

/// <summary>On-disk shape of <see cref="CardShareRefreshState"/>: hashes, numbers and a fingerprint only.</summary>
internal sealed class CardShareRefreshFile
{
    public string Account { get; set; } = "";
    public DateTime NextDueUtc { get; set; }
    public long DelayTicks { get; set; }
    public int Cursor { get; set; }
    public Dictionary<string, string> Confirmed { get; set; } = [];
    public Dictionary<string, string> Sent { get; set; } = [];
}
