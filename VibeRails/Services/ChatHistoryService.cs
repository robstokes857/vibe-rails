using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services;

public class ChatHistoryService(IDbService dbService) : IChatHistoryService
{
    public async Task<ChatHistoryResponse> GetHistoryAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var offset = (page - 1) * pageSize;
        var items = await dbService.GetChatHistoryPageAsync(pageSize, offset, cancellationToken);
        return new ChatHistoryResponse(items, page, pageSize);
    }
}
