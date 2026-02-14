using VibeRails.DB;
using VibeRails.DTOs;

namespace VibeRails.Routes;

public static class ProjectRoutes
{
    public static void Map(WebApplication app, string launchDirectory)
    {
        app.MapGet("/api/v1/IsLocal", () =>
        {
            return Results.Ok(new IsLocalResponse(
                IsLocalContext: Utils.ParserConfigs.IsLocalContext(),
                LaunchDirectory: launchDirectory,
                RootPath: Utils.ParserConfigs.GetRootPath()
            ));
        }).WithName("IsLocal");

        // PUT /api/v1/projects/name - Set custom project name (stored in AgentMetadata table)
        app.MapPut("/api/v1/projects/name", async (
            IRepository repository,
            UpdateAgentNameRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request.Path))
            {
                return Results.BadRequest(new ErrorResponse("Path is required"));
            }

            if (string.IsNullOrEmpty(request.CustomName))
            {
                return Results.BadRequest(new ErrorResponse("CustomName is required"));
            }

            await repository.SetProjectCustomNameAsync(request.Path, request.CustomName, cancellationToken);

            return Results.Ok(new UpdateAgentNameResponse(request.Path, request.CustomName));
        }).WithName("UpdateProjectName");

        // GET /api/v1/projects/name?path={path} - Get custom project name
        app.MapGet("/api/v1/projects/name", async (
            IRepository repository,
            string path,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(path))
            {
                return Results.BadRequest(new ErrorResponse("Path is required"));
            }

            var customName = await repository.GetProjectCustomNameAsync(path, cancellationToken);
            return Results.Ok(new UpdateAgentNameResponse(path, customName ?? ""));
        }).WithName("GetProjectName");
    }
}
