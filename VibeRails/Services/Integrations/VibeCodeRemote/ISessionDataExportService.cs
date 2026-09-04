namespace VibeRails.Services.Integrations.VibeCodeRemote;

public interface ISessionDataExportService
{
    /// <summary>True when both a saved API key and a valid absolute HTTPS export URL exist.</summary>
    bool IsConfigured { get; }

    /// <summary>Exports and acknowledges one ended, unexported session.</summary>
    Task<SessionDataExportResult> ExportSessionAsync(
        string sessionId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Best-effort removal of one session's spool file. Called when the user deletes the
    /// session locally, so the erased material does not survive on disk as a spooled copy.
    /// Silent on failure: a spool held open by an in-flight upload is reclaimed by
    /// <see cref="SweepOrphanedSpoolAsync"/> instead.
    /// </summary>
    void DeleteSpool(string sessionId);

    /// <summary>
    /// Deletes every spool file whose session no longer awaits export. This is the part that
    /// actually closes the class of leak: a targeted delete alone cannot, because a session
    /// deleted while its envelope is mid-serialization removes a file that does not exist yet.
    /// Returns the number of files removed.
    /// </summary>
    Task<int> SweepOrphanedSpoolAsync(CancellationToken cancellationToken);
}

public enum SessionDataExportStatus
{
    Success,
    NotFound,
    NoApiKey,
    NotConfigured,
    Busy,
    InvalidApiKey,
    UploadFailed,
    Failed
}

public sealed record SessionDataExportResult(
    SessionDataExportStatus Status,
    string SessionId,
    string? Sha256 = null,
    string? Detail = null);
