using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using ModelContextProtocol.Server;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Board;

namespace VibeRails.Services.Mcp;

/// <summary>Tracks desktop MCP work separately from terminal sessions and their launch ownership.</summary>
public sealed class DesktopMcpActivityTracker : BackgroundService
{
    internal static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(90);
    internal static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(20);
    private const int MaximumClients = 512;
    private readonly IBoardStore store;
    private readonly bool stdio;
    private readonly Func<string, string?> readEnvironment;
    private readonly TimeProvider clock;
    private readonly string runtimeId = Guid.NewGuid().ToString("N");
    private readonly Dictionary<string, DateTime> clients = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool stopping;

    /// <summary>Creates a process-lifetime tracker. Only stdio renews activity without another tool call.</summary>
    public DesktopMcpActivityTracker(IBoardStore store, bool stdio)
        : this(store, stdio, Environment.GetEnvironmentVariable, TimeProvider.System) { }

    internal DesktopMcpActivityTracker(IBoardStore store, bool stdio, Func<string, string?> readEnvironment, TimeProvider clock)
    {
        this.store = store;
        this.stdio = stdio;
        this.readEnvironment = readEnvironment;
        this.clock = clock;
    }

    /// <summary>A session ID without a terminal tab is still desktop activity, not a live terminal.</summary>
    public bool IsDesktop => string.IsNullOrWhiteSpace(readEnvironment(LocalToolApiContext.CurrentTabIdVariable));

    /// <summary>Returns whether this transport can identify one caller rather than an app-name aggregate.</summary>
    public bool IsAggregate(McpServer? server) => !stdio && string.IsNullOrWhiteSpace(server?.SessionId);

    /// <summary>Best-effort activity after an already-successful Board write; never changes that write's result.</summary>
    public async Task<bool> TrackAsync(McpServer? server, string project, string cardId, string label,
        CancellationToken cancellationToken = default)
    {
        if (!IsDesktop) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await gate.WaitAsync(timeout.Token);
            try
            {
                if (stopping) return false;
                var now = clock.GetUtcNow().UtcDateTime;
                foreach (var expired in clients.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToArray())
                    clients.Remove(expired);
                var id = ClientId(server, label);
                if (!clients.ContainsKey(id) && clients.Count >= MaximumClients) return false;
                var expiry = now + LeaseDuration;
                if (!await store.StartDesktopActivityAsync(project, cardId, id,
                        BoardPromptComposer.SanitizeLine(label, 60), expiry, timeout.Token)) return false;
                clients[id] = expiry;
                return true;
            }
            finally { gate.Release(); }
        }
        catch (Exception)
        {
            // Presence must not turn a committed Board edit into a failure or leak request details.
            return false;
        }
    }

    /// <summary>Ends only this connection's card activity. Stateless app-name aggregates expire naturally.</summary>
    public async Task<bool> EndAsync(McpServer? server, string cardId, string label, CancellationToken cancellationToken = default)
    {
        if (!IsDesktop || IsAggregate(server)) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await gate.WaitAsync(timeout.Token);
            try { await store.EndDesktopActivityAsync(ClientId(server, label), cardId, timeout.Token); }
            finally { gate.Release(); }
            return true;
        }
        catch (Exception) { return false; }
    }

    internal string ClientId(McpServer? server, string label)
    {
        if (stdio) return runtimeId + ":stdio";
        var aggregate = IsAggregate(server);
        var identity = aggregate ? BoardPromptComposer.SanitizeLine(server?.ClientInfo?.Name ?? label, 60) : server!.SessionId!;
        return runtimeId + (aggregate ? ":app:" : ":session:")
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24];
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!stdio) return;
        using var timer = new PeriodicTimer(HeartbeatInterval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) await RenewAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    internal async Task RenewAsync(CancellationToken cancellationToken)
    {
        if (!stdio || !IsDesktop) return;
        try
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                if (stopping) return;
                var expiry = clock.GetUtcNow().UtcDateTime + LeaseDuration;
                foreach (var id in clients.Keys.ToArray())
                {
                    await store.RenewDesktopActivityAsync(id, expiry, cancellationToken);
                    clients[id] = expiry;
                }
            }
            finally { gate.Release(); }
        }
        catch (Exception) { /* A failed heartbeat expires naturally; it never affects Board work. */ }
    }

    /// <summary>Expires this host's active marks on clean disconnect; the lease covers abrupt process termination.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await gate.WaitAsync(timeout.Token);
            try
            {
                stopping = true;
                foreach (var id in clients.Keys)
                    await store.EndDesktopActivityAsync(id, null, timeout.Token);
                clients.Clear();
            }
            finally { gate.Release(); }
        }
        catch (Exception) { /* Crash/timeout cleanup is bounded by the lease. */ }
    }
}
