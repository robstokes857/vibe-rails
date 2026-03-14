using VibeRails.Interfaces;

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
    }
}
