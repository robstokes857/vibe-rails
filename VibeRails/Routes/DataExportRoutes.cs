using VibeRails.DTOs;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Routes;

public static class DataExportRoutes
{
    public static void Map(WebApplication app)
    {
        // The remote server's status is a domain result, not the local VibeRails
        // authentication status. Always return HTTP 200 so an upstream 401/403 cannot
        // trigger the SPA's local-session bootstrap flow.
        app.MapPost("/api/v1/settings/export-data", async (
            IDataExportService dataExportService,
            CancellationToken cancellationToken) =>
        {
            var result = await dataExportService.ExportAsync(cancellationToken);
            return Results.Ok(ToResponse(result));
        }).WithName("ExportData");

        // Polled a few times a second while the export POST above is still in flight, so it stays
        // allocation-light and never touches the database or the file system.
        app.MapGet("/api/v1/settings/export-data/progress", (
            IDataExportProgress progress) =>
        {
            var snapshot = progress.Current;
            return Results.Ok(new DataExportProgressResponse(
                snapshot.Active,
                snapshot.RunId,
                snapshot.Stage,
                snapshot.ProcessedBytes,
                snapshot.TotalBytes,
                snapshot.ElapsedMs,
                snapshot.StageElapsedMs));
        }).WithName("ExportDataProgress");
    }

    internal static DataExportResponse ToResponse(DataExportResult result)
    {
        var (status, message) = result.Status switch
        {
            DataExportStatus.Success =>
                ("ok", "Data exported successfully."),
            DataExportStatus.NoApiKey =>
                ("no_api_key", "Save a valid API key before exporting data."),
            DataExportStatus.NotConfigured =>
                ("not_configured", "Data export is not configured."),
            DataExportStatus.Busy =>
                ("busy", "A data export is already in progress."),
            DataExportStatus.InvalidApiKey =>
                ("invalid_api_key", "The export server rejected the API key."),
            DataExportStatus.UploadFailed =>
                ("upload_failed", result.Detail ?? "Failed to upload the data export."),
            _ =>
                ("failed", result.Detail ?? "Failed to export data.")
        };

        return new DataExportResponse(
            result.Status == DataExportStatus.Success,
            status,
            message,
            result.Sha256);
    }
}
