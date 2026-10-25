namespace VibeRails.DB;

/// <summary>A persisted, immutable batch awaiting a matching hosted receipt.</summary>
public sealed record TokenSavingsPendingReport(
    string ReportId, string ComputerId, string ComputerName, long TokensSaved);

/// <summary>Tracks incremental uploads independently of API keys and computer names.</summary>
public interface ITokenSavingsPublishStore
{
    /// <summary>
    /// Returns the existing pending report or creates one from persisted savings. The first
    /// call establishes the existing lifetime counter as a baseline and returns no report.
    /// </summary>
    Task<TokenSavingsPendingReport?> PrepareAsync(string computerName, CancellationToken cancellationToken = default);

    /// <summary>Atomically advances the checkpoint and clears the matching pending report.</summary>
    Task AcknowledgeAsync(string reportId, CancellationToken cancellationToken = default);
}
