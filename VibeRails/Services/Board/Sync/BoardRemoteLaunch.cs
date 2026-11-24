using Serilog;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Services.Board.Sync;

/// <summary>An open project root advertising its published boards to their owner.</summary>
public sealed record BoardLaunchPoll(Guid InstanceId, string Name, List<Guid> BoardIds, bool AcceptLaunch = true);
/// <summary>A single-use request identifying a saved card and the required sync sequence.</summary>
public sealed record BoardLaunchCommand(Guid Id, Guid BoardId, string CardId, long RequiredSeq);
/// <summary>A poll can consume at most one launch command.</summary>
public sealed record BoardLaunchPollResult(BoardLaunchCommand? Command);
/// <summary>The outcome returned by the exact desktop instance that received the command.</summary>
public sealed record BoardLaunchResult(Guid InstanceId, string Status, string? SessionId, string? Reason = null);
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
            if (!board.SyncEnabled) continue;
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
            // The plan refused the sync: waiting cannot change that, and nothing is launched from a stale card.
            catch (BoardSyncClientException ex) when (ex.Code == ProDenial.Code) { return Result("sync_failed"); }
        }
        if (status is null || status.LastError is not null || status.Cursor < command.RequiredSeq || status.Unsent != 0)
            return Result("sync_failed");
        if (!await store.IsCardSyncAppliedAsync(project, link.BoardId, command.CardId, status.Cursor, ct))
            return Result("sync_failed");
        var currentLink = await store.GetSyncLinkAsync(project, link.BoardId, ct);
        var currentBoard = await store.GetBoardAsync(project, link.BoardId, ct);
        var card = await store.FindCardAsync(project, command.CardId, ct);
        if (currentBoard is not { SyncEnabled: true } || currentLink is not { Imported: false, Enabled: true } || currentLink.RemoteBoardId != link.RemoteBoardId
            || currentLink.DestinationKey != destination || client.DestinationKey != destination
            || card is null || card.Id != command.CardId || card.BoardId != link.BoardId) return Result("unavailable");
        try
        {
            var result = await launch.LaunchAsync(project, card.Id, null, ct);
            return result is null ? Result("unavailable") : Result("started", result.SessionId);
        }
        // VIBE-104: the web offers "Go to agent" for the agent already running instead of a second launch.
        catch (BoardConflictException ex) { return Result("busy", ex.SessionId); }
        catch (BoardValidationException ex) { return new(instance, "failed", null, ex.RemoteLaunchReason ?? "invalid_launch"); }
    }
}

/// <summary>
/// Outbound live control, only while this project's root backend is open. Remote Boards are a Pro
/// feature of the hosted service: while the account's plan is known not to include them the poll
/// is skipped, and a <c>pro_required</c> answer is logged once, not as a Warning every ten seconds.
/// </summary>
public sealed class BoardRemoteLaunchHostedService(IServiceScopeFactory scopes, IBoardSyncClient client,
    IAccountEntitlements? entitlements = null) : BackgroundService
{
    private readonly Guid instance = Guid.NewGuid();
    private readonly Dictionary<Guid, DateTime> handled = [];
    private DateTime proRecheckUtc = DateTime.MinValue;
    private bool proPauseLogged;
    private Task activeLaunch = Task.CompletedTask;
    private string? pollState;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            do { await PollOnceAsync(stoppingToken); }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { await WaitForLaunchAsync(); }
    }

    internal Task WaitForLaunchAsync() => activeLaunch;

    /// <summary>One poll. Only cancellation escapes; every failure is logged and left for the next turn.</summary>
    internal async Task PollOnceAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (client.IsConfigured && client.DestinationKey is { } destination)
                await PollAsync(destination, stoppingToken);
            else ReportState("not_configured");
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (BoardSyncClientException ex) when (ex.Code == ProDenial.Code)
        {
            // This account's denial goes into the entitlement snapshot (the HTTP client has already
            // noted it; repeating it is harmless), so the next polls are skipped without a request.
            // Any other payer waits here before it is asked again.
            if (ex.Payer == ProDenial.BoardOwnerPayer || entitlements is null) proRecheckUtc = DateTime.UtcNow + BoardSyncProPause.OwnerRecheck;
            else entitlements.NoteDenied(ex.Feature ?? ProFeatures.RemoteBoards);
            if (!proPauseLogged) Log.Information("[BoardRemoteLaunch] Remote Start work is paused: Vibe Rails Pro is required ({Payer})", ex.Payer);
            proPauseLogged = true;
        }
        catch (BoardSyncClientException ex)
        {
            // A 404 can also mean an advertised board is unavailable. Never hide it as
            // an assumed older server: otherwise a live root silently disappears.
            Log.Warning("[BoardRemoteLaunch] Poll failed: {Code} (HTTP {Status})", ex.Code, ex.Status);
            pollState = null;
        }
        catch (Exception ex) { Log.Warning(ex, "[BoardRemoteLaunch] Live launch poll failed"); }
    }

    private async Task PollAsync(string destination, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IBoardStore>();
        var project = await scope.ServiceProvider.GetRequiredService<IBoardProjectResolver>().ResolveAsync(ct);
        var ids = new List<Guid>();
        foreach (var board in await store.GetBoardsAsync(project, ct))
        {
            if (!board.SyncEnabled) continue;
            var link = await store.GetSyncLinkAsync(project, board.Id, ct);
            if (link is { Imported: false, Enabled: true } && link.DestinationKey == destination
                && Guid.TryParse(link.RemoteBoardId, out var remote) && remote != Guid.Empty && !ids.Contains(remote)) ids.Add(remote);
            if (ids.Count == 100) break;
        }
        if (ids.Count == 0) { ReportState("no_eligible_boards", project); return; }
        // Asked only when there is a board to advertise; the answer is cached between polls.
        if (DateTime.UtcNow < proRecheckUtc || entitlements is not null && !await entitlements.AllowsAsync(ProFeatures.RemoteBoards, ct))
        { ReportState("plan_paused", project, ids.Count); return; }
        var label = Environment.MachineName + " · " + Path.GetFileName(project.TrimEnd(Path.DirectorySeparatorChar));
        if (label.Length > 100) label = label[..100];
        var accepting = activeLaunch.IsCompleted;
        var response = await client.PollLaunchAsync(new(instance, label, ids, accepting), ct, destination);
        ReportState("registered", project, ids.Count);
        proPauseLogged = false;
        if (response.Command is not { } command || client.DestinationKey != destination || !ids.Contains(command.BoardId)) return;
        foreach (var id in handled.Where(p => DateTime.UtcNow - p.Value > TimeSpan.FromMinutes(15)).Select(p => p.Key).ToList()) handled.Remove(id);
        if (handled.Count >= 1024 || !handled.TryAdd(command.Id, DateTime.UtcNow)) return;
        if (!accepting)
        {
            // Older servers ignore AcceptLaunch. Decline a new command rather than
            // running two launches concurrently or replaying it later.
            await client.CompleteLaunchAsync(command.Id, new(instance, "busy", null), ct, destination);
            return;
        }
        activeLaunch = ProcessCommandAsync(project, destination, command, ct);
    }

    private void ReportState(string state, string? project = null, int boards = 0)
    {
        var next = $"{state}|{project}|{boards}";
        if (pollState == next) return;
        pollState = next;
        Log.Information("[BoardRemoteLaunch] Discovery {State}; project={Project}; eligibleBoards={BoardCount}", state, project, boards);
    }

    private async Task ProcessCommandAsync(string project, string destination, BoardLaunchCommand command, CancellationToken ct)
    {
        try { await LaunchAndCompleteAsync(project, destination, command, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { Log.Warning(ex, "[BoardRemoteLaunch] Launch result delivery failed"); }
    }

    private async Task LaunchAndCompleteAsync(string project, string destination, BoardLaunchCommand command, CancellationToken ct)
    {
        // The worker owns its scope until sync, launch and result delivery finish.
        // Polling continues independently and never starts another worker meanwhile.
        await using var scope = scopes.CreateAsyncScope();
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
            result = new(instance, "failed", null, "desktop_error");
        }
        // Only the acknowledgement is retried. Never replay a command whose result was lost.
        for (var attempt = 0; ; attempt++)
        {
            try { await client.CompleteLaunchAsync(command.Id, result, ct, destination); break; }
            catch (BoardSyncClientException) when (attempt < 2 && client.DestinationKey == destination) { await Task.Delay(1000, ct); }
        }
    }
}
