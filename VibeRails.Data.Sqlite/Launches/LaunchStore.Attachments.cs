using VibeRails.Launches;

namespace VibeRails.Data.Sqlite;

public sealed partial class LaunchStore
{
    public async Task<IReadOnlyList<LaunchAttachment>> ListAttachmentsAsync(string definitionId, CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT Id,Name,MimeType,Size,CreatedUTC FROM DefinitionAttachments WHERE DefinitionId=$id AND RemovedUTC IS NULL ORDER BY CreatedUTC,Id;", null, ("$id", definitionId));
        using var r = await cmd.ExecuteReaderAsync(ct);
        List<LaunchAttachment> results = [];
        while (await r.ReadAsync(ct)) results.Add(new(r.GetString(0), definitionId, r.GetString(1), r.GetString(2), r.GetInt64(3), Date(r.GetString(4))));
        return results;
    }

    public async Task<LaunchAttachment> AddAttachmentAsync(string definitionId, string name, string mimeType, byte[] content, CancellationToken ct = default)
    {
        using var db = Open();
        using var tx = db.BeginTransaction(deferred: false);
        using (var owner = Command(db, "SELECT 1 FROM LaunchPresets WHERE Id=$id AND RetiredUTC IS NULL;", tx, ("$id", definitionId)))
            if (await owner.ExecuteScalarAsync(ct) is null) throw new LaunchValidationException("The attachment owner is unavailable.");
        using (var count = Command(db, "SELECT count(*) FROM DefinitionAttachments WHERE DefinitionId=$id AND RemovedUTC IS NULL;", tx, ("$id", definitionId)))
            if (Convert.ToInt64(await count.ExecuteScalarAsync(ct)) >= 40) throw new LaunchValidationException("A definition can have at most 40 current attachments.");
        var attachment = new LaunchAttachment("att_" + Guid.NewGuid().ToString("N"), definitionId, name, mimeType, content.LongLength, DateTime.UtcNow);
        using var cmd = Command(db, "INSERT INTO DefinitionAttachments(Id,DefinitionId,Name,MimeType,Size,CreatedUTC,Content) VALUES($id,$owner,$name,$mime,$size,$now,$content);", tx,
            ("$id", attachment.Id), ("$owner", definitionId), ("$name", name), ("$mime", mimeType),
            ("$size", content.LongLength), ("$now", Stamp(attachment.CreatedUtc)), ("$content", content));
        await cmd.ExecuteNonQueryAsync(ct);
        tx.Commit();
        return attachment;
    }

    public async Task<LaunchAttachmentContent?> ReadAttachmentAsync(string definitionId, string attachmentId, CancellationToken ct = default)
        => await ReadAttachmentCoreAsync(definitionId, attachmentId, false, ct);

    public async Task<LaunchAttachmentContent?> ReadRunAttachmentAsync(string project, string runId, string attachmentId, CancellationToken ct = default)
    {
        var run = await GetRunAsync(runId, ct);
        if (run is null || run.ProjectPath != project || run.Snapshot.AttachmentIds?.Contains(attachmentId) != true) return null;
        return await ReadAttachmentCoreAsync(run.DefinitionId, attachmentId, true, ct);
    }

    private async Task<LaunchAttachmentContent?> ReadAttachmentCoreAsync(string definitionId, string attachmentId, bool retained, CancellationToken ct)
    {
        using var db = Open();
        using var cmd = Command(db, "SELECT Name,MimeType,Size,CreatedUTC,Content FROM DefinitionAttachments WHERE Id=$id AND DefinitionId=$owner AND (RemovedUTC IS NULL OR $retained=1);", null,
            ("$id", attachmentId), ("$owner", definitionId), ("$retained", retained));
        using var r = await cmd.ExecuteReaderAsync(ct);
        return await r.ReadAsync(ct) ? new(new(attachmentId, definitionId, r.GetString(0), r.GetString(1), r.GetInt64(2), Date(r.GetString(3))), (byte[])r[4]) : null;
    }

    public async Task<bool> RemoveAttachmentAsync(string definitionId, string attachmentId, CancellationToken ct = default)
    {
        using var db = Open();
        using var cmd = Command(db, "UPDATE DefinitionAttachments SET RemovedUTC=$now WHERE Id=$id AND DefinitionId=$owner AND RemovedUTC IS NULL;", null,
            ("$now", Stamp(DateTime.UtcNow)), ("$id", attachmentId), ("$owner", definitionId));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }
}
