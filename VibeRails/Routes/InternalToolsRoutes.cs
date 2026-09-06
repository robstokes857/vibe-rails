using VibeRails.DTOs;
using VibeRails.Services.Diagnostics;

namespace VibeRails.Routes;

/// <summary>Read-only diagnostics, mapped only by the authenticated active root backend.</summary>
public static class InternalToolsRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/internal/logs", async (IFeatureLogReader log, IDiagnosticLogReader diagnostics,
            string? source, string? feature, string? level, string? status, string? search, string? operationId,
            int? offset, int? limit, CancellationToken cancellationToken) =>
        {
            var query = new FeatureLogQuery(feature, level, status, search,
                operationId, offset ?? 0, limit ?? 100);
            var selectedSource = string.IsNullOrWhiteSpace(source) ? "features" : source.Trim().ToLowerInvariant();
            if (selectedSource == "features")
                return Results.Ok(await log.ReadAsync(query, cancellationToken: cancellationToken));
            if (selectedSource is "application" or "daemon")
                return Results.Ok(await diagnostics.ReadAsync(selectedSource, query, cancellationToken));

            return Results.BadRequest(new ErrorResponse("Source must be features, application, or daemon."));
        })
            .WithName("GetInternalFeatureLogs");

        app.MapGet("/api/v1/internal/uploads", async (IFeatureLogReader log,
            string? status, string? search, int? offset, int? limit,
            CancellationToken cancellationToken) =>
            Results.Ok(await log.ReadAsync(new FeatureLogQuery(Status: status, Search: search,
                Offset: offset ?? 0, Limit: limit ?? 100), uploadsOnly: true,
                cancellationToken: cancellationToken)))
            .WithName("GetInternalUploads");
    }
}
