using System.Net.WebSockets;
using VibeRails.DTOs;
using VibeRails.Services.Terminal;

namespace VibeRails.Routes;

public static class TerminalTabsRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/terminal/tabs", async (
            ITerminalTabHostService tabHost,
            CancellationToken cancellationToken) =>
        {
            var tabs = await tabHost.ListTabsAsync(cancellationToken);
            return Results.Ok(new TerminalTabListResponse(tabs.ToList(), tabHost.MaxTabs));
        }).WithName("ListTerminalTabs");

        app.MapPost("/api/v1/terminal/tabs", async (
            ITerminalTabHostService tabHost,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var tab = await tabHost.CreateTabAsync(cancellationToken);
                return Results.Ok(tab);
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).WithName("CreateTerminalTab");

        app.MapDelete("/api/v1/terminal/tabs/{tabId}", async (
            string tabId,
            ITerminalTabHostService tabHost,
            CancellationToken cancellationToken) =>
        {
            var removed = await tabHost.DeleteTabAsync(tabId, cancellationToken);
            if (!removed)
            {
                return Results.NotFound(new ErrorResponse($"Terminal tab not found: {tabId}"));
            }

            return Results.Ok(new MessageResponse($"Terminal tab closed: {tabId}"));
        }).WithName("DeleteTerminalTab");

        app.MapGet("/api/v1/terminal/tabs/{tabId}/status", async (
            string tabId,
            ITerminalTabHostService tabHost,
            CancellationToken cancellationToken) =>
        {
            var status = await tabHost.GetStatusAsync(tabId, cancellationToken);
            if (status == null)
            {
                return Results.NotFound(new ErrorResponse($"Terminal tab not found: {tabId}"));
            }

            return Results.Ok(status);
        }).WithName("GetTerminalTabStatus");

        app.MapPost("/api/v1/terminal/tabs/{tabId}/start", async (
            string tabId,
            ITerminalTabHostService tabHost,
            StartTerminalRequest? request,
            CancellationToken cancellationToken) =>
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Cli))
            {
                return Results.BadRequest(new ErrorResponse("CLI type is required"));
            }

            try
            {
                var status = await tabHost.StartSessionAsync(tabId, request, cancellationToken);
                return Results.Ok(status);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new ErrorResponse($"Terminal tab not found: {tabId}"));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).WithName("StartTerminalTabSession");

        app.MapPost("/api/v1/terminal/tabs/{tabId}/stop", async (
            string tabId,
            ITerminalTabHostService tabHost,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var status = await tabHost.StopSessionAsync(tabId, cancellationToken);
                return Results.Ok(status);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound(new ErrorResponse($"Terminal tab not found: {tabId}"));
            }
            catch (InvalidOperationException ex)
            {
                return Results.BadRequest(new ErrorResponse(ex.Message));
            }
        }).WithName("StopTerminalTabSession");

        app.Map("/api/v1/terminal/tabs/{tabId}/ws", async (
            HttpContext context,
            string tabId,
            ITerminalTabHostService tabHost) =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("WebSocket connections only");
                return;
            }

            var status = await tabHost.GetStatusAsync(tabId, context.RequestAborted);
            if (status == null)
            {
                context.Response.StatusCode = 404;
                await context.Response.WriteAsync($"Terminal tab not found: {tabId}");
                return;
            }

            if (!status.HasActiveSession)
            {
                context.Response.StatusCode = 400;
                await context.Response.WriteAsync("No active terminal session in this tab.");
                return;
            }

            using var webSocket = await context.WebSockets.AcceptWebSocketAsync();
            try
            {
                await tabHost.HandleWebSocketProxyAsync(tabId, webSocket, context.RequestAborted);
            }
            catch (Exception ex)
            {
                if (webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        await webSocket.CloseAsync(
                            WebSocketCloseStatus.InternalServerError,
                            ex.Message,
                            CancellationToken.None);
                    }
                    catch
                    {
                        // Best-effort only.
                    }
                }
            }
        });
    }
}
