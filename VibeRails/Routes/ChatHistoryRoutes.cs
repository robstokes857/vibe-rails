using VibeRails.DB;
using VibeRails.Interfaces;
using VibeRails.DTOs;
using Serilog;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Routes;

public static class ChatHistoryRoutes
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v1/chatHistory", async (
            IChatHistoryService chatHistoryService,
            int? page,
            int? pageSize,
            string? preferredWorkingDirectory,
            string? sortBy,
            string? sortDirection,
            CancellationToken cancellationToken) =>
        {
            var clampedPage = Math.Max(1, page ?? 1);
            var clampedPageSize = Math.Clamp(pageSize ?? 20, 1, 100);
            var result = await chatHistoryService.GetHistoryAsync(
                clampedPage,
                clampedPageSize,
                preferredWorkingDirectory,
                sortBy,
                sortDirection,
                cancellationToken);
            return Results.Ok(result);
        }).WithName("GetChatHistory");

        app.MapGet("/api/v1/chatHistory/{sessionId}", async (
            IChatHistoryService chatHistoryService,
            string sessionId,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return Results.BadRequest(new ErrorResponse("Session id is required."));
            }

            var item = await chatHistoryService.GetSessionAsync(sessionId, cancellationToken);
            return item is null
                ? Results.NotFound(new ErrorResponse("Chat history entry not found."))
                : Results.Ok(item);
        }).WithName("GetChatHistorySession");

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

        app.MapGet("/api/v1/chatHistory/{sessionId}/raw-session", (IRepository repository, string sessionId) =>
            new SessionRecordingResult(repository, sessionId, SessionRecordingKind.Raw)).WithName("GetChatHistoryRawSession");

        app.MapGet("/api/v1/chatHistory/{sessionId}/replay", (IRepository repository, string sessionId) =>
            new SessionRecordingResult(repository, sessionId, SessionRecordingKind.Replay)).WithName("GetChatHistoryReplay");

        app.MapGet("/api/v1/chatHistory/{sessionId}/terminal-replay", (IRepository repository, string sessionId) =>
            new SessionRecordingResult(repository, sessionId, SessionRecordingKind.TerminalReplay)).WithName("GetTerminalReplay");

        app.MapGet("/api/v1/chatHistory/{sessionId}/summary", async (
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
           catch (RemoteSummaryException ex)
           {
               // A JSON answer keeps the CORS headers, so Send To shows this text instead of
               // reporting that the local host cannot be reached (VIBE-157).
               Log.Warning("[ChatHistory] Summary failed: local {Status}, viberails.ai {UpstreamStatus}",
                   ex.StatusCode, ex.UpstreamStatus);
               return Results.Json(new ErrorResponse(ex.Message), statusCode: ex.StatusCode);
           }
       }).WithName("GetChatHistorySummary");
    }
}
