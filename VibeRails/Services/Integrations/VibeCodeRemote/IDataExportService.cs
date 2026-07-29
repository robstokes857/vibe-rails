namespace VibeRails.Services.Integrations.VibeCodeRemote;

public interface IDataExportService
{
    Task<DataExportResult> ExportAsync(CancellationToken cancellationToken);
}

public enum DataExportStatus
{
    Success,
    NoApiKey,
    NotConfigured,
    Busy,
    InvalidApiKey,
    UploadFailed,
    Failed
}

public sealed record DataExportResult(
    DataExportStatus Status,
    string? Sha256 = null,
    string? Detail = null);
