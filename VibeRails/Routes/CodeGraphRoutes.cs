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
            if (files.Length > RepositoryCodeGraph.MaxPriorityFiles || files.Any(path => !RepositoryCodeGraph.IsSafePath(path)))
                return Results.BadRequest(new ErrorResponse("Supply at most 1000 repository-relative report paths."));
            var root = await git.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(root)) return Results.BadRequest(new ErrorResponse("Not in a git repository."));
            try { return Results.Ok(await graph.ReadAsync(root, files, cancellationToken)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new ErrorResponse(ex.Message)); }
        }).WithName("GetCodeReportGraph");

        // GET /api/v1/code-analyzer/changes - the working tree's changes against HEAD: statuses,
        // staging and line counts for the Code quality card. Read-only; the root is server-derived.
        app.MapGet("/api/v1/code-analyzer/changes", async (IGitService git, WorkingTreeChanges changes, CancellationToken cancellationToken) =>
        {
            var root = await git.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(root)) return Results.BadRequest(new ErrorResponse("Not in a git repository."));
            try { return Results.Ok(await changes.ListAsync(root, cancellationToken)); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new ErrorResponse(ex.Message)); }
        }).WithName("GetWorkingTreeChanges");

        // GET /api/v1/code-analyzer/changes/diff?path=... - HEAD and working-tree text of one
        // repository-relative path, bounded, in the shared diff viewer's shape. The same safe-path
        // rules as graph priorities apply before git or the file system is touched.
        app.MapGet("/api/v1/code-analyzer/changes/diff", async (string? path, string? original, IGitService git, WorkingTreeChanges changes, CancellationToken cancellationToken) =>
        {
            var relativePath = (path ?? string.Empty).Replace('\\', '/').Trim();
            if (!RepositoryCodeGraph.IsSafePath(relativePath))
                return Results.BadRequest(new ErrorResponse("A repository-relative path is required."));
            // A renamed entry names its HEAD-side path explicitly; it is validated like the path itself.
            var originalPath = string.IsNullOrWhiteSpace(original) ? null : original.Replace('\\', '/').Trim();
            if (originalPath is not null && !RepositoryCodeGraph.IsSafePath(originalPath))
                return Results.BadRequest(new ErrorResponse("The original path must be repository-relative."));
            var root = await git.GetRootPathAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(root)) return Results.BadRequest(new ErrorResponse("Not in a git repository."));
            try
            {
                var diff = await changes.ReadDiffAsync(root, relativePath, cancellationToken, originalPath);
                return diff is null ? Results.NotFound(new ErrorResponse("That path has no readable change.")) : Results.Ok(diff);
            }
            catch (InvalidOperationException ex) { return Results.BadRequest(new ErrorResponse(ex.Message)); }
        }).WithName("GetWorkingTreeChangeDiff");
    }
}
