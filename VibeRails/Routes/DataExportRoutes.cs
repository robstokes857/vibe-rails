namespace VibeRails.Routes;

public static class DataExportRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/settings/backups", (
            [Microsoft.AspNetCore.Mvc.FromServices] VibeRails.Services.Backups.CompleteBackupService backups,
            HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(backups.GetCoverage(), VibeRails.Services.Backups.BackupJson.Default.BackupCoverage);
        }).WithName("CompleteBackupCoverage");
    }
}
