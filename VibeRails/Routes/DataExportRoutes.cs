namespace VibeRails.Routes;

public static class DataExportRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/settings/backups", (
            [Microsoft.AspNetCore.Mvc.FromServices] VibeRails.Services.Backups.CompleteBackupService backups,
            [Microsoft.AspNetCore.Mvc.FromServices] VibeRails.Services.Board.Backup.BoardDataBackupService boardBackups,
            HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Json(backups.GetCoverage() with { BoardDataBackup = boardBackups.GetStatus() },
                VibeRails.Services.Backups.BackupJson.Default.BackupCoverage);
        }).WithName("CompleteBackupCoverage");
    }
}
