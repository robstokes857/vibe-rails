using VibeRails.Services.Board.Sync;

namespace VibeRails.Routes;

public static partial class BoardRoutes
{
    private static void MapRemoteBoards(WebApplication app)
    {
        var routes = app.MapGroup("/api/v1/board/remote");
        routes.AddEndpointFilter((context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            return next(context);
        });
        routes.MapGet("", (RemoteBoardsService boards, int? offset, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await boards.ListAsync(offset ?? 0, ct))));
        routes.MapPost("", (RemoteBoardsService boards, RemoteBoardWriteRequest request, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await boards.CreateAsync(request, ct))));
        routes.MapPut("/{id}", (RemoteBoardsService boards, string id, RemoteBoardWriteRequest request, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await boards.RenameAsync(id, request, ct))));
        routes.MapPost("/{id}/delete", (RemoteBoardsService boards, string id, RemoteBoardWriteRequest request, CancellationToken ct) =>
            RunAsync(async () => Results.Ok(await boards.DeleteAsync(id, request, ct))));
    }
}
