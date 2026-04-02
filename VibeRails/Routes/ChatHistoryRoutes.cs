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

        app.MapGet("/api/v1/chatHistory/{sessionId}/terminal-replay", async (
            IRepository repository,
            string sessionId,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(sessionId))
                return Results.BadRequest(new ErrorResponse("Session id is required."));

            // Legacy SessionLogs — fine-grained per-chunk with timestamps, used for animated replay
            var legacyChunks = await repository.GetSessionLogChunksAsync(sessionId, cancellationToken);
            if (legacyChunks.Count == 0)
                return Results.NotFound(new ErrorResponse("No session replay data found."));

            // Build frames with relative timing from legacy chunks
            var startTime = legacyChunks[0].TimestampUtc;
            var frames = legacyChunks.Select(c => new TerminalReplayFrame(
                Convert.ToBase64String(c.Content),
                (long)(c.TimestampUtc - startTime).TotalMilliseconds
            )).ToList();

            // Enriched TerminalSessionLogs — buffered with cols/rows/alt screen metadata
            var enrichedLogs = await repository.GetTerminalSessionLogsAsync(sessionId, cancellationToken);
            var enrichedChunks = enrichedLogs.Select(l => new TerminalReplayChunk(
                l.Sequence,
                l.IsAlternateScreen,
                Convert.ToBase64String(l.Data),
                l.Cols,
                l.Rows
            )).ToList();

            // Use enriched chunk dimensions for initial terminal size (fall back to 120x40)
            var initialCols = enrichedChunks.Count > 0 ? enrichedChunks[0].Cols : 120;
            var initialRows = enrichedChunks.Count > 0 ? enrichedChunks[0].Rows : 40;

            return Results.Ok(new TerminalReplayResponse(sessionId, initialCols, initialRows, enrichedChunks, frames));
        }).WithName("GetTerminalReplay");

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
