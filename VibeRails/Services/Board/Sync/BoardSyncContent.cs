using System.Security.Cryptography;
using System.Text.Json;

namespace VibeRails.Services.Board.Sync;

/// <summary>Backs up complete saved content independently of bounded activity previews.</summary>
internal static class BoardSyncContent
{
    internal const int MaxContentBytes = 128 * 1024 * 1024;
    internal const int MaxItems = 2000;
    private const int PartBytes = 4 * 1024 * 1024;

    internal static async Task<BoardSyncActivityWire> CaptureAsync(IBoardStore store, IBoardSyncClient client,
        BoardSyncActivityCache cache, string identity, BoardSyncLinkRecord link, string cardId,
        BoardSyncActivityRecord metadata, CancellationToken ct)
    {
        if (metadata.Commits.Count > MaxItems || metadata.Attachments.Count > MaxItems)
            throw new BoardValidationException("Complete card content exceeds the supported item count; nothing was acknowledged.");
        var preview = await BoardSyncActivity.CaptureAsync(store, link.ProjectPath, cardId, metadata, ct);
        // Require a v2 ACK even for an empty replacement, so older writers cannot erase complete-content references.
        if (metadata.Commits.Count == 0 && metadata.Attachments.Count == 0) return preview with { Schema = 2 };
        var commits = new List<BoardSyncCommitWire>();
        foreach (var commit in metadata.Commits.OrderByDescending(c => c.CommittedUtc))
        {
            ct.ThrowIfCancellationRequested();
            var snapshot = await store.GetSyncCommitSnapshotAsync(link.ProjectPath, cardId, commit.Sha, MaxContentBytes, ct);
            BoardSyncContentWire? content = null;
            if (snapshot is not null)
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, BoardSyncJsonContext.Default.SandboxDiffResponse);
                content = await UploadAsync(client, cache, identity, link.DestinationKey, bytes, ct);
            }
            // Full file content is fetched on demand; it is never truncated into a display snapshot.
            commits.Add(new(commit.Sha, Clip(commit.Author, 500), Clip(commit.Message, 16000), commit.CommittedUtc, [], content));
        }
        var attachments = new List<BoardSyncAttachmentWire>();
        foreach (var attachment in metadata.Attachments)
        {
            if (attachment.Bytes is < 0 or > MaxContentBytes)
                throw new BoardValidationException("An attachment exceeds the supported complete-content size; it remains local and will not be acknowledged.");
            var bytes = await store.GetSyncAttachmentContentAsync(link.ProjectPath, cardId, attachment.Id, MaxContentBytes, ct);
            if (bytes is null || bytes.LongLength != attachment.Bytes)
                throw new BoardValidationException("Complete attachment content is unavailable; its upload will retry without acknowledging an incomplete copy.");
            var content = await UploadAsync(client, cache, identity, link.DestinationKey, bytes, ct);
            attachments.Add(new(attachment.Id, Clip(attachment.Name, 255), Clip(attachment.MimeType, 100), attachment.Bytes,
                attachment.CreatedUtc, null, null, content));
        }
        preview.Warnings.RemoveAll(w => w.Contains("commit", StringComparison.OrdinalIgnoreCase) || w.Contains("attachment", StringComparison.OrdinalIgnoreCase));
        var result = preview with { Schema = 2, Commits = commits, Attachments = attachments };
        if (JsonSerializer.SerializeToUtf8Bytes(result, BoardSyncJsonContext.Default.BoardSyncActivityWire).Length > BoardSyncActivity.MaxSnapshotBytes)
            throw new BoardValidationException("Complete card references exceed the transfer limit; the upload remains pending.");
        return result;
    }

    internal static async Task<BoardSyncContentWire> UploadAsync(IBoardSyncClient client, BoardSyncActivityCache cache,
        string identity, string? destination, byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length > MaxContentBytes) throw new BoardValidationException("Saved content exceeds 128 MiB; no incomplete backup was acknowledged.");
        var parts = new List<BoardSyncContentPartWire>();
        for (var offset = 0; offset < bytes.Length; offset += PartBytes)
        {
            var buffer = bytes.AsMemory(offset, Math.Min(PartBytes, bytes.Length - offset));
            var sha = Convert.ToHexStringLower(SHA256.HashData(buffer.Span));
            var part = new BoardSyncContentPartWire(sha, buffer.Length);
            var key = identity + ":content-part:" + sha;
            var now = cache.UtcNow;
            if (cache.Get(key) is not { } previous || previous.UploadedUtc < now.AddHours(-1))
            {
                await client.PutContentPartAsync(part, buffer, ct, destination);
                cache.Set(key, new(sha, now, now));
            }
            parts.Add(part);
        }
        return new(Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.LongLength, parts);
    }

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length];
}
