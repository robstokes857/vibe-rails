using System.Security.Cryptography;
using System.Text;
using VibeRails.Utils;

namespace VibeRails.Services.Board.Sync;

/// <summary>A local copy associated with the configured account's remote board.</summary>
public sealed record RemoteBoardLocalCopy(string BoardId, string Name, string ProjectName, bool SyncEnabled);
/// <summary>Remote board metadata and its current local associations.</summary>
public sealed record RemoteBoardRow(string Id, string Name, bool IsOwner, IReadOnlyList<string> Lanes,
    string? Url, IReadOnlyList<RemoteBoardLocalCopy> LocalCopies);
/// <summary>A bounded page; Context prevents an old screen from writing to a different account.</summary>
public sealed record RemoteBoardPage(string Context, IReadOnlyList<RemoteBoardRow> Boards, int? NextOffset);
/// <summary>A management command bound to the destination displayed in the remote board list.</summary>
public sealed record RemoteBoardWriteRequest(string? Context, string? Name = null, string? RequestId = null);
/// <summary>Confirmation of a remote change, optionally identifying the newly created board.</summary>
public sealed record RemoteBoardActionResult(string Message, string? Id = null, string? Url = null);

/// <summary>Human management of hosted boards, including remote copies whose local board was removed.</summary>
public sealed class RemoteBoardsService(IBoardStore store, IBoardSyncClient client, BoardSyncLock syncLock)
{
    public const int PageSize = 100;

    /// <summary>Lists accessible remote boards and matches local copies across projects for this account only.</summary>
    public async Task<RemoteBoardPage> ListAsync(int offset, CancellationToken ct)
    {
        if (offset < 0 || offset > int.MaxValue - PageSize || offset % PageSize != 0)
            throw new BoardValidationException("Invalid remote board page.");
        var destination = Destination();
        var remote = await client.DiscoverPageAsync(offset, ct, destination);
        var locals = await LocalLinksAsync(destination, ct);
        CheckDestination(destination);
        var rows = remote.Select(board => new RemoteBoardRow(board.RemoteBoardId, board.Name, board.IsOwner,
            board.Lanes.OrderBy(l => l.Position).Select(l => l.Name).ToList(),
            BoardSyncEndpoint.BoardPage(ParserConfigs.GetFrontendUrl(), board.RemoteBoardId),
            locals.Where(l => l.Link.RemoteBoardId.Equals(board.RemoteBoardId, StringComparison.OrdinalIgnoreCase))
                .Select(l => new RemoteBoardLocalCopy(l.Board.Id, l.Board.Name,
                    Path.GetFileName(Path.TrimEndingDirectorySeparator(l.Board.ProjectPath)), l.Board.SyncEnabled)).ToList())).ToList();
        return new(Context(destination), rows, remote.Count == PageSize ? offset + PageSize : null);
    }

    /// <summary>Creates a remote-only board; the client retains RequestId for safe retries.</summary>
    public async Task<RemoteBoardActionResult> CreateAsync(RemoteBoardWriteRequest request, CancellationToken ct)
    {
        var destination = CheckedContext(request.Context);
        var name = Name(request.Name);
        if (request.RequestId is not { Length: 32 } id || id.Any(c => !char.IsAsciiHexDigit(c)))
            throw new BoardValidationException("A valid create request id is required.");
        var identity = "remote_" + id.ToLowerInvariant();
        string[] names = ["Backlog", "Ready", "Build", "Review", "Done"];
        var lanes = names.Select((label, index) => new BoardSyncLaneWire(identity + "_" + index, label, null, index)).ToList();
        var result = await client.PublishAsync(new(identity, name, "WEB", lanes, "WEB"), ct, destination);
        if (result.RemoteBoardId == Guid.Empty)
            throw new BoardSyncClientException("The server did not confirm the new board. Refresh before retrying.", "invalid_response");
        var remoteId = result.RemoteBoardId.ToString("D");
        return new("Remote board created. Open it to manage its cards.", remoteId,
            BoardSyncEndpoint.BoardPage(ParserConfigs.GetFrontendUrl(), remoteId));
    }

    /// <summary>Renames an owned remote board and its linked local copies so sync keeps the new name.</summary>
    public async Task<RemoteBoardActionResult> RenameAsync(string id, RemoteBoardWriteRequest request, CancellationToken ct)
    {
        var name = Name(request.Name);
        var destination = CheckedContext(request.Context);
        using var held = Acquire();
        await RequireOwnerAsync(id, destination, ct);
        var locals = await LocalLinksAsync(destination, ct);
        await client.PushAsync(id, new(name, null, null, []), ct, destination);
        try
        {
            foreach (var local in locals.Where(l => l.Link.RemoteBoardId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                await store.RenameBoardAsync(local.Board.ProjectPath, local.Board.Id, name, null, ct);
        }
        catch (Exception)
        {
            return new("Remote board renamed, but a local copy could not be renamed. Check its name before syncing.");
        }
        return new("Remote board renamed.");
    }

    /// <summary>Pauses associated local copies before deleting the owner's hosted board.</summary>
    public async Task<RemoteBoardActionResult> DeleteAsync(string id, RemoteBoardWriteRequest request, CancellationToken ct)
    {
        var destination = CheckedContext(request.Context);
        using var held = Acquire();
        await RequireOwnerAsync(id, destination, ct);
        var locals = await LocalLinksAsync(destination, ct);
        CheckDestination(destination);
        foreach (var local in locals.Where(l => l.Link.RemoteBoardId.Equals(id, StringComparison.OrdinalIgnoreCase)))
            await store.SetBoardSyncEnabledAsync(local.Board.ProjectPath, local.Board.Id, false, ct);
        try { await client.DeleteRemoteBoardAsync(id, ct, destination); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new BoardValidationException("Remote deletion was not confirmed. Refresh before retrying. Linked local boards are kept with sync paused.");
        }
        return new("Remote board deleted. Linked local boards are kept with sync paused.");
    }

    private async Task RequireOwnerAsync(string id, string destination, CancellationToken ct)
    {
        if (!Guid.TryParseExact(id, "D", out var parsed) || parsed == Guid.Empty)
            throw new BoardValidationException("Invalid remote board.");
        var board = await client.DescribeAsync(id, ct, destination);
        if (board is null || !board.RemoteBoardId.Equals(id, StringComparison.OrdinalIgnoreCase) || !board.IsOwner)
            throw new BoardValidationException("Only the owner can rename or delete this remote board.");
        CheckDestination(destination);
    }

    private async Task<List<(BoardRecord Board, BoardSyncLinkRecord Link)>> LocalLinksAsync(string destination, CancellationToken ct)
    {
        var result = new List<(BoardRecord, BoardSyncLinkRecord)>();
        foreach (var board in await store.GetLocalBoardsAsync(ct))
        {
            var link = await store.GetSyncLinkAsync(board.ProjectPath, board.Id, ct);
            if (link is not null && link.DestinationKey == destination) result.Add((board, link));
        }
        return result;
    }

    private IDisposable Acquire() => syncLock.TryAcquire()
        ?? throw new BoardValidationException("A Board sync is running. Try again when it finishes.");
    private string Destination() => client.DestinationKey ?? throw new BoardValidationException("Sign in to viberails.ai to manage remote boards.");
    private void CheckDestination(string expected)
    {
        if (client.DestinationKey != expected) throw new BoardValidationException("The account changed. Refresh remote boards before continuing.");
    }
    private string CheckedContext(string? context)
    {
        var destination = Destination();
        if (context != Context(destination)) throw new BoardValidationException("The account changed. Refresh remote boards before continuing.");
        return destination;
    }
    private static string Context(string destination) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("remote-boards\n" + destination)));
    private static string Name(string? name) => name?.Trim() is { Length: > 0 and <= 120 } text
        && !text.Any(char.IsControl) ? text : throw new BoardValidationException("Enter a board name of 1 to 120 characters on one line.");
}
