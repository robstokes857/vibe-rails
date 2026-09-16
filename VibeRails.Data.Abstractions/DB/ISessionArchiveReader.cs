using VibeRails.DTOs;

namespace VibeRails.DB;

public interface ISessionArchiveReader
{
    /// <summary>
    /// Oldest ended session whose independent data-export acknowledgement is absent and whose
    /// retry backoff (if any) has elapsed, together with its recorded attempt count.
    /// </summary>
    Task<UnexportedSessionRef?> GetOldestUnexportedSessionAsync(DateTime endedBeforeUtc, DateTime nowUtc, CancellationToken cancellationToken);

    /// <summary>
    /// Records one failed attempt and defers the next until <paramref name="nextAttemptUtc"/>.
    /// Never marks the session exported: an undelivered session stays undelivered, it just
    /// stops holding the head of the queue while it backs off.
    /// </summary>
    Task<bool> DeferSessionExportAsync(string sessionId, DateTime nextAttemptUtc, CancellationToken cancellationToken);

    /// <summary>Streams one complete session envelope from a consistent read snapshot.</summary>
    Task<SessionDataExportDescriptor?> WriteSessionExportAsync(string sessionId, Stream destination, CancellationToken cancellationToken);

    /// <summary>True while a spool file for this session is still worth keeping.</summary>
    Task<bool> SessionAwaitsExportAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>
    /// Sets ExportedUTC once; does not touch the transcript Processed flag. <paramref name="proxyCoverage"/>
    /// is the acknowledged envelope's proxyCoverage.status and <paramref name="proxyMaxRowId"/> the
    /// largest proxy rowid that envelope contained; together they are the ONLY evidence retention
    /// will accept that a given proxy exchange was actually backed up. ExportedUTC alone never was:
    /// a v1 envelope carries no proxy data at all, a v2 envelope whose proxy read failed is still
    /// acknowledged with status "unavailable", and an exchange the proxy's write queue lands after
    /// the snapshot keeps the earlier CreatedUTC it was queued with. Pass null when unknown.
    /// </summary>
    Task<bool> MarkSessionExportedAsync(string sessionId, DateTime exportedUtc, string? proxyCoverage,
        long? proxyMaxRowId, CancellationToken cancellationToken);
}
