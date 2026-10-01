using Serilog;

namespace VibeRails.Services.Board.Sync;

/// <summary>An open project root advertising its published boards to their owner.</summary>
public sealed record BoardLaunchPoll(Guid InstanceId, string Name, List<Guid> BoardIds);
/// <summary>A single-use request identifying a saved card and the required sync sequence.</summary>
public sealed record BoardLaunchCommand(Guid Id, Guid BoardId, string CardId, long RequiredSeq);
/// <summary>A poll can consume at most one launch command.</summary>
public sealed record BoardLaunchPollResult(BoardLaunchCommand? Command);
/// <summary>The outcome returned by the exact desktop instance that received the command.</summary>
public sealed record BoardLaunchResult(Guid InstanceId, string Status, string? SessionId);
/// <summary>The hosted service's acknowledgement of a launch outcome.</summary>
public sealed record BoardLaunchResultAck(bool Saved);

/// <summary>Resolves remote launch identity locally and uses the ordinary card launch path.</summary>
public sealed class BoardRemoteLaunchService(IBoardStore store, IBoardSyncClient client,
    IBoardSyncService sync, IBoardLaunchService launch)
{
    /// <summary>Synchronizes and validates a card before launching it with its saved settings.</summary>
    public async Task<BoardLaunchResult> LaunchAsync(string project, Guid instance, BoardLaunchCommand command,
        string destination, CancellationToken ct)
    {
        BoardLaunchResult Result(string status, string? session = null) => new(instance, status, session);
        if (command.Id == Guid.Empty || command.BoardId == Guid.Empty || command.CardId is not { Length: > 0 and <= 64 }
            || command.RequiredSeq < 0 || client.DestinationKey != destination) return Result("unavailable");
        var boards = await store.GetBoardsAsync(project, ct);
        BoardSyncLinkRecord? link = null;
        foreach (var board in boards)
        {
            var candidate = await store.GetSyncLinkAsync(project, board.Id, ct);
            if (candidate is { Imported: false, Enabled: true } && candidate.DestinationKey == destination
                && Guid.TryParse(candidate.RemoteBoardId, out var remote) && remote == command.BoardId)
            { link = candidate; break; }
        }
        if (link is null) return Result("unavailable");
        // Sync can already be running in another root. Wait briefly for its normal lock rather
        // than launching from a stale card. No launch occurs until the requested sequence arrived.
        BoardSyncStatus? status = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try { status = await sync.SyncNowAsync(project, link.BoardId, ct); break; }
            catch (BoardValidationException) when (attempt < 19) { await Task.Delay(500, ct); }
            catch (BoardValidationException) { return Result("sync_failed"); }
        }
        if (status is null || status.LastError is not null || status.Cursor < command.RequiredSeq || status.Unsent != 0)
            return Result("sync_failed");
        if (!await store.IsCardSyncAppliedAsync(project, link.BoardId, command.CardId, status.Cursor, ct))
            return Result("sync_failed");
        var currentLink = await store.GetSyncLinkAsync(project, link.BoardId, ct);
        var card = await store.FindCardAsync(project, command.CardId, ct);
        if (currentLink is not { Imported: false, Enabled: true } || currentLink.RemoteBoardId != link.RemoteBoardId
            || currentLink.DestinationKey != destination || client.DestinationKey != destination
            || card is null || card.Id != command.CardId || card.BoardId != link.BoardId) return Result("unavailable");
        try
        {
            var result = await launch.LaunchAsync(project, card.Id, null, ct);
            return result is null ? Result("unavailable") : Result("started", result.SessionId);
        }
        catch (BoardConflictException) { return Result("busy"); }
        catch (BoardValidationException) { return Result("failed"); }
    }
}

/// <summary>Outbound live control, only while this project's root backend is open.</summary>
public sealed class BoardRemoteLaunchHostedService(IServiceScopeFactory scopes, IBoardSyncClient client) : BackgroundService
{
    private readonly Guid instance = Guid.NewGuid();
    private readonly Dictionary<Guid, DateTime> handled = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (client.IsConfigured && client.DestinationKey is { } destination)
                    await PollAsync(destination, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (BoardSyncClientException ex)
            {
                // Old hosted versions answer 404 until the companion release is deployed.
                if (ex.Status != 404) Log.Warning("[BoardRemoteLaunch] Poll failed: {Code}", ex.Code);
            }
            catch (Exception ex) { Log.Warning(ex, "[BoardRemoteLaunch] Live launch poll failed"); }
            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    private async Task PollAsync(string destination, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IBoardStore>();
        var project = await scope.ServiceProvider.GetRequiredService<IBoardProjectResolver>().ResolveAsync(ct);
        var ids = new List<Guid>();
        foreach (var board in (await store.GetBoardsAsync(project, ct)).Take(100))
        {
            var link = await store.GetSyncLinkAsync(project, board.Id, ct);
            if (link is { Imported: false, Enabled: true } && link.DestinationKey == destination
                && Guid.TryParse(link.RemoteBoardId, out var remote)) ids.Add(remote);
        }
        if (ids.Count == 0) return;
        var label = Environment.MachineName + " · " + Path.GetFileName(project.TrimEnd(Path.DirectorySeparatorChar));
        if (label.Length > 100) label = label[..100];
        var response = await client.PollLaunchAsync(new(instance, label, ids), ct, destination);
        if (response.Command is not { } command || client.DestinationKey != destination || !ids.Contains(command.BoardId)) return;
        foreach (var id in handled.Where(p => DateTime.UtcNow - p.Value > TimeSpan.FromMinutes(15)).Select(p => p.Key).ToList()) handled.Remove(id);
        if (handled.Count >= 1024 || !handled.TryAdd(command.Id, DateTime.UtcNow)) return;
        BoardLaunchResult result;
        try
        {
            result = await scope.ServiceProvider.GetRequiredService<BoardRemoteLaunchService>()
                .LaunchAsync(project, instance, command, destination, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Log.Warning(ex, "[BoardRemoteLaunch] Card launch failed");
            result = new(instance, "failed", null);
        }
        // Only the acknowledgement is retried. Never replay a command whose result was lost.
        for (var attempt = 0; ; attempt++)
        {
            try { await client.CompleteLaunchAsync(command.Id, result, ct, destination); break; }
            catch (BoardSyncClientException) when (attempt < 2 && client.DestinationKey == destination) { await Task.Delay(1000, ct); }
        }
    }
}
