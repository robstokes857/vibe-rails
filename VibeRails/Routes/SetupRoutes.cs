using VibeRails.Services.Setup;

namespace VibeRails.Routes;

/// <summary>
/// Setup page: which tools this machine has, and a private terminal that installs one from the
/// fixed catalog. Root-only, like the script terminal it shares its PTY lifetime with.
/// </summary>
public static class SetupRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/setup/status", async (ISetupStatusService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.GetAsync(cancellationToken)))
            .WithName("GetSetupStatus");

        app.MapPost("/api/v1/setup/status/refresh", async (ISetupStatusService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.RefreshAsync(cancellationToken)))
            .WithName("RefreshSetupStatus");

        app.MapGet("/api/v1/setup/install/ws", async (HttpContext context, IHostApplicationLifetime lifetime) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
                context.RequestAborted, lifetime.ApplicationStopping);
            using var socket = await context.WebSockets.AcceptWebSocketAsync(
                context.Items["viberails_accepted_subprotocol"] as string);
            await SetupInstallTerminalSession.RunAsync(socket, cancellation.Token);
        }).WithName("RunSetupInstallTerminal");
    }
}
