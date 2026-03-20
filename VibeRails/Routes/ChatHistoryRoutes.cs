using VibeRails.Interfaces;
using VibeRails.DTOs;

namespace VibeRails.Routes;

public static class ChatHistoryRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/chatHistory", async (
            IChatHistoryService chatHistoryService,
            int? page,
            int? pageSize,
            CancellationToken cancellationToken) =>
        {
            var clampedPage = Math.Max(1, page ?? 1);
            var clampedPageSize = Math.Clamp(pageSize ?? 20, 1, 100);
            var result = await chatHistoryService.GetHistoryAsync(clampedPage, clampedPageSize, cancellationToken);
            return Results.Ok(result);
        }).WithName("GetChatHistory");

        app.MapPatch("/api/v1/chatHistory/{sessionId}", async (
            IChatHistoryService chatHistoryService,
            string sessionId,
            UpdateChatHistorySessionRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Results.BadRequest(new ErrorResponse("Session id is required."));
            }

            var sessionDisplayName = request.SessionDisplayName?.Trim();
            if (string.IsNullOrWhiteSpace(sessionDisplayName))
            {
                return Results.BadRequest(new ErrorResponse("SessionDisplayName is required."));
            }

            var renamed = await chatHistoryService.RenameSessionAsync(sessionId, sessionDisplayName, cancellationToken);
            return renamed
                ? Results.Ok(new MessageResponse("Chat renamed."))
                : Results.NotFound(new ErrorResponse("Chat history entry not found."));
        }).WithName("RenameChatHistory");

        app.MapDelete("/api/v1/chatHistory/{sessionId}", async (
            IChatHistoryService chatHistoryService,
            string sessionId,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Results.BadRequest(new ErrorResponse("Session id is required."));
            }

            var deleted = await chatHistoryService.DeleteSessionAsync(sessionId, cancellationToken);
            return deleted
                ? Results.Ok(new MessageResponse("Chat deleted."))
                : Results.NotFound(new ErrorResponse("Chat history entry not found."));
        }).WithName("DeleteChatHistory");
    }
}
