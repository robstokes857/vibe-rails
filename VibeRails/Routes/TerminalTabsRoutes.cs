using System.Net.WebSockets;
using Serilog;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Terminal;


namespace VibeRails.Routes;

public static class TerminalTabsRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/terminal/tabs", async (
            ITerminalTabHostService tabHost,
            IBoardStore boardStore,
            CancellationToken cancellationToken) =>
        {
            var tabs = await tabHost.ListTabsAsync(cancellationToken);
            var withCards = await WithBoardCardsAsync(tabs, boardStore, cancellationToken);
            return Results.Ok(new TerminalTabListResponse(withCards, tabHost.MaxTabs));
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

            var acceptedSubprotocol = context.Items["viberails_accepted_subprotocol"] as string;
            using var webSocket = await context.WebSockets.AcceptWebSocketAsync(acceptedSubprotocol);

            int? cols = null;
            int? rows = null;
            if (context.Request.Query.TryGetValue("cols", out var colsStr) && int.TryParse(colsStr, out var c) && c > 0)
                cols = c;
            if (context.Request.Query.TryGetValue("rows", out var rowsStr) && int.TryParse(rowsStr, out var r) && r > 0)
                rows = r;

            try
            {
                await tabHost.HandleWebSocketProxyAsync(tabId, webSocket, cols, rows, context.RequestAborted);
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

    // VIBE-36: a tab names the Board card its session is linked to (Start work, Chat with agent,
    // a lane Automation, an agent touching the card), so the terminal can link back to the story.
    // A session's primary link sorts first. The browser treats a failed list as "no tabs", so a
    // board.db failure drops only the card links, never the list.
    internal static async Task<List<TerminalTabStatusResponse>> WithBoardCardsAsync(
        IReadOnlyList<TerminalTabStatusResponse> tabs,
        IBoardStore boardStore,
        CancellationToken cancellationToken)
    {
        var sessionIds = tabs.Select(tab => tab.SessionId).OfType<string>().Where(id => id.Length > 0).ToArray();
        if (sessionIds.Length == 0) return tabs.ToList();

        IReadOnlyList<BoardSessionCard> links;
        try
        {
            links = await boardStore.GetSessionCardsAsync(sessionIds, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "[TerminalTabs] Could not read Board card links for terminal tabs");
            return tabs.ToList();
        }

        var cardBySession = new Dictionary<string, BoardSessionCard>(StringComparer.Ordinal);
        foreach (var link in links) cardBySession.TryAdd(link.SessionId, link);
        return tabs.Select(tab => tab.SessionId is { } sessionId && cardBySession.TryGetValue(sessionId, out var card)
                ? tab with { BoardCard = new TerminalTabBoardCard(card.CardId, card.Key, card.Title, card.DisplayId) }
                : tab)
            .ToList();
    }
}
