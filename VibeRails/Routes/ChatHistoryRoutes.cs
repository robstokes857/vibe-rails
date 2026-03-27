using VibeRails.DB;
using VibeRails.Interfaces;
using VibeRails.DTOs;
using Microsoft.AspNetCore.Http.HttpResults;

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


        app.MapGet("/api/v1/chatHistory/{sessionId}/transcript", async (
            ISessionTranscriptService transcriptService,
            string sessionId,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return Results.BadRequest(new ErrorResponse("Session id is required."));

            var text = await transcriptService.GetOrBuildAsync(sessionId, cancellationToken);
            return Results.Ok(new ChatHistoryTranscriptResponse(sessionId, text));
        }).WithName("GetChatHistoryTranscript");

        app.MapGet("/api/v1/chatHistory/{sessionId}/replay", async (
            IRepository repository,
            string sessionId,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return Results.BadRequest(new ErrorResponse("Session id is required."));

            var chunks = await repository.GetSessionLogChunksAsync(sessionId, cancellationToken);
            var totalLength = chunks.Sum(c => c.Content.Length);
            var combined = new byte[totalLength];
            var offset = 0;
            foreach (var chunk in chunks)
            {
                chunk.Content.CopyTo(combined, offset);
                offset += chunk.Content.Length;
            }
            return Results.Ok(new ChatHistoryReplayResponse(
                sessionId,
                Convert.ToBase64String(combined)
            ));
        }).WithName("GetChatHistoryReplay");

        app.MapGet("/api/v1/chatHistory/{sessionId}/Summary", async (
           IChatHistoryService chatHistoryService,
           string sessionId,
           bool? regenerate,
           CancellationToken cancellationToken) =>
       {
           if (string.IsNullOrWhiteSpace(sessionId))
               return Results.BadRequest(new ErrorResponse("Session id is required."));

           try
           {
               var result = await chatHistoryService.GetSummaryAsync(sessionId, regenerate == true, cancellationToken);
               return Results.Ok(result);
           }
           catch (KeyNotFoundException)
           {
               return Results.NotFound(new ErrorResponse("Chat history entry not found."));
           }
       }).WithName("GetChatHistorySummary");
    }
}
