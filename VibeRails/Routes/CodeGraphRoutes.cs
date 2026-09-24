using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.CodeReports;

namespace VibeRails.Routes;

/// <summary>Read-only report graph on the existing authenticated root backend.</summary>
public static class CodeGraphRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/v1/code-analyzer/graph", async (CodeGraphRequest request,
            IGitService git, RepositoryCodeGraph graph, CancellationToken cancellationToken) =>
        {
            var files = request.Files ?? [];
            if (files.Length > RepositoryCodeGraph.MaxFiles || files.Any(path => !RepositoryCodeGraph.IsSafePath(path)))
                return Results.BadRequest(new ErrorResponse("Supply at most 1000 repository-relative report paths."));
            var root = await git.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(root)) return Results.BadRequest(new ErrorResponse("Not in a git repository."));
            try { return Results.Ok(await graph.ReadAsync(root, files, cancellationToken)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new ErrorResponse(ex.Message)); }
        }).WithName("GetCodeReportGraph");
    }
}
