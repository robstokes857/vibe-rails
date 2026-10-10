using System.Security.Cryptography;
using System.Text.Json;
using VibeRails.DB;
using VibeRails.Services.Board.Sync;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;

namespace VibeRails.Services.Board.Sharing;

/// <summary>Publishes only explicitly selected saved cards and refreshes existing, creator-key-bound publications.</summary>
public sealed class CardSharePublisher(IBoardStore boards, CardShareCapture capture, CardShareClient client,
    ISessionArchiveReader archives, BoardSyncLock syncLock, CardShareRefreshState refreshState)
{
    public Task<CardShareResult> ListAsync(string project, string identity, int? before, CancellationToken ct) => RunAsync(async () =>
    {
        var card = await CardAsync(project, identity, ct);
        var key = Key();
        if (before is <= 0) throw new BoardValidationException("Invalid sharing-link cursor.");
        var links = await client.ListAsync(key, card.Key, card.Id, before, ct);
        RequireSameAccount(key);
        return new(true, "", Links: links, NextBefore: links.Count == 100 ? links[^1].Id : null);
    }, ct);

    public Task<CardShareResult> CreateAsync(string project, string identity, string? displayName, CancellationToken ct) => RunAsync(async () =>
    {
        var name = Name(displayName);
        var key = Key();
        using var held = syncLock.TryAcquire() ?? throw new BoardValidationException("A Board sync is running. Try sharing again when it finishes.");
        var card = await CardAsync(project, identity, ct);
        var snapshot = await capture.CaptureAsync(project, card.Id, ct);
        RequireSameAccount(key);
        var result = await client.PublishAsync(key, name, card.Id, snapshot, ct);
        // Publication may already exist even if queuing fails or the client closes. The scheduler
        // rediscovers it from this exact key; never retry this creation POST automatically.
        var queued = await QueueAsync(key, result.UploadSessions, ct);
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
        using var held = syncLock.TryAcquire() ?? throw new BoardValidationException("A Board sync is running. Try again shortly.");
        var snapshot = await capture.CaptureAsync(project, card.Id, ct);
        var count = 0; var after = 0; var allQueued = true;
        while (true)
        {
            RequireSameAccount(key);
            var sources = await client.SourcesAsync(key, after, ct);
            foreach (var source in sources.Where(s => s.SourceKey == card.Key && s.LocalCardId == card.Id))
            {
                RequireSameAccount(key);
                var result = await client.RefreshAsync(key, source, snapshot, ct);
                allQueued &= await QueueAsync(key, result.UploadSessions, ct);
                count++;
            }
            if (sources.Count < 100) break;
            after = sources[^1].Id;
        }
        return new(count > 0, count == 0 ? "No active shares were created with this account key. Create a link or sign in with its original key."
            : allQueued ? "Shared card updated. Linked recordings become playable after they end and upload."
            : "Shared card updated, but some recordings are unavailable on this computer.");
    }, ct);

    /// <summary>One bounded page per scheduler cycle. A missing local source never deletes a hosted copy.</summary>
    public async Task RefreshDueAsync(CancellationToken ct)
    {
        var key = ParserConfigs.GetApiKey();
        if (string.IsNullOrWhiteSpace(key)) return;
        using var held = syncLock.TryAcquire();
        if (held is null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        ct = deadline.Token;
        var account = SessionSharingService.KeyFingerprint(key);
        var sources = await client.SourcesAsync(key, refreshState.Cursor(account), ct);
        foreach (var source in sources)
        {
            RequireSameAccount(key);
            try
            {
                // A remote source key is only a lookup. Its stored project owns every subsequent read.
                var local = await boards.FindLocalCardAsync(source.LocalCardId ?? source.SourceKey, ct);
                if (local is null || local.Key != source.SourceKey) continue;
                var snapshot = await capture.CaptureAsync(local.ProjectPath, local.Id, ct);
                var hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot, CardSharingJsonContext.Default.CardShareSnapshot)));
                if (refreshState.IsCurrent(account, source, hash)) continue;
                RequireSameAccount(key);
                var result = await client.RefreshAsync(key, source, snapshot, ct);
                await QueueAsync(key, result.UploadSessions, ct);
                // A recording absent locally needs restoration/manual refresh, not repeated
                // transfer of the same complete card and documents on every scheduler tick.
                refreshState.Remember(account, source.Id, hash);
            }
            catch (BoardValidationException) { /* Preserve the last complete publication; manual refresh explains the failure. */ }
            catch (CardShareTransportException) { /* One unavailable publication must not starve the rest. */ }
            finally { refreshState.Advance(account, source.Id); }
        }
        if (sources.Count < 100) refreshState.Advance(account, 0);
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

/// <summary>Bounded process memory only: avoids retransferring unchanged complete documents every tick.</summary>
public sealed class CardShareRefreshState
{
    private readonly Lock _gate = new();
    private string _account = "";
    private int _cursor;
    private readonly Dictionary<int, string> _acknowledged = [];
    private void Account(string account) { if (_account != account) { _account = account; _cursor = 0; _acknowledged.Clear(); } }
    public int Cursor(string account) { lock (_gate) { Account(account); return _cursor; } }
    public void Advance(string account, int cursor) { lock (_gate) { Account(account); _cursor = cursor; } }
    public bool IsCurrent(string account, CardShareSource source, string hash)
    { lock (_gate) { Account(account); return hash == source.Revision && _acknowledged.GetValueOrDefault(source.Id) == hash; } }
    public void Remember(string account, int id, string hash)
    { lock (_gate) { Account(account); if (_acknowledged.Count >= 10000) _acknowledged.Clear(); _acknowledged[id] = hash; } }
}
