using VibeRails.Launches;
using VibeRails.Services.Board;

namespace VibeRails.Services.Launches;

/// <summary>Owner-aware attachment adapter shared by HTTP and MCP; no Board rows are created.</summary>
public sealed class LaunchAttachments(ILaunchStore store)
{
    public Task<LaunchAttachment> AddAsync(string id, LaunchAttachmentRequest request, CancellationToken ct)
    {
        var bytes = BoardService.DecodeAttachmentDataUrl(request.DataUrl);
        var name = BoardService.NormalizeAttachmentName(request.Name);
        return store.AddAttachmentAsync(id, name, BoardService.DetectAttachmentMimeType(name, bytes), bytes, ct);
    }
}
