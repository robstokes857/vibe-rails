using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Jobs;
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Services.Workspaces;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

public sealed class TerminalTabHostService : ITerminalTabHostService, IAsyncDisposable
{
    private const int StartupTimeoutSeconds = 30;
    private const int HealthAttempts = 30;
    private const int HealthDelayMs = 500;

    // An Automation's pre-launch steps run inside the child and can legitimately take minutes, so
    // the ceiling is generous. It exists because "slow" and "wedged" look identical from here: a
    // CLI sitting on an auth prompt never exits, so HasExited alone would poll forever.
    private static readonly TimeSpan ActiveSessionTimeout = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ActiveSessionPollMin = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ActiveSessionPollMax = TimeSpan.FromSeconds(2);

    // Teardown runs without the caller's token (see TerminateChildAsync), so the graceful stop
    // needs a bound of its own rather than HttpClient's 100-second default. A child that cannot
    // answer within this is one the hard kill below is for.
    private static readonly TimeSpan GracefulStopRequestTimeout = TimeSpan.FromSeconds(5);

    private sealed record TerminalChildProcess(
        string TabId,
        Process Process,
        int Port,
        string BootstrapUrl,
        string SessionToken,
        string TabToken,
        DateTime CreatedUtc,
        string? AutomationRunId = null);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILocalClientTracker _localClientTracker;
    private readonly IAppEventBus _appEventBus;
    private readonly ILocalToolApiContext _toolApiContext;
    private readonly ITokenSavingsStore _tokenSavings;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SemaphoreSlim _createGate = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TerminalChildProcess> _tabs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _tabRelayCts = new(StringComparer.Ordinal);
    private readonly string _launchDirectory;
    private readonly string _tabsOwnerId;
    private bool _tabsOwnerAcquired;

    public int MaxTabs => 8;

    public TerminalTabHostService(
        IHttpClientFactory httpClientFactory,
        ILocalClientTracker localClientTracker,
        IAppEventBus appEventBus,
        ILocalToolApiContext toolApiContext,
        ITokenSavingsStore tokenSavings,
        IServiceScopeFactory scopeFactory)
    {
        _httpClientFactory = httpClientFactory;
        _localClientTracker = localClientTracker;
        _appEventBus = appEventBus;
        _toolApiContext = toolApiContext;
        _tokenSavings = tokenSavings;
        _scopeFactory = scopeFactory;
        _launchDirectory = Directory.GetCurrentDirectory();
        _tabsOwnerId = $"terminal-tabs:{Environment.ProcessId}";
    }

    public async Task<IReadOnlyList<TerminalTabStatusResponse>> ListTabsAsync(CancellationToken cancellationToken = default)
    {
        TerminalChildProcess[] snapshot;
        lock (_lock)
        {
            snapshot = _tabs.Values.OrderBy(t => t.CreatedUtc).ToArray();
        }

        if (snapshot.Length == 0)
        {
            return [];
        }

        var tasks = snapshot.Select(child => BuildTabStatusAsync(child, cancellationToken)).ToArray();
        var statuses = await Task.WhenAll(tasks);
        return statuses.OrderBy(t => t.CreatedUTC).ToArray();
    }

    public async Task<TerminalTabStatusResponse> CreateTabAsync(CancellationToken cancellationToken = default)
        => await CreateTabCoreAsync(null, null, waitForActiveSession: false, cancellationToken);

    /// <summary>
    /// Starts a manually-triggered Automation inside a terminal-tab child instead of opening a
    /// separate native terminal. The child still runs <see cref="JobRunner"/>, so cancellation,
    /// deadlines, idle completion, session linking, and run history stay identical to scheduled
    /// runs; only the viewer is the bidirectional Web UI terminal.
    /// </summary>
    public async Task<TerminalTabStatusResponse> CreateAutomationTabAsync(
        string runId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runId))
            throw new InvalidOperationException("Automation run id is required.");

        await using var scope = _scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IJobStore>();
        var repository = scope.ServiceProvider.GetRequiredService<IRepository>();

        var finalizeReservedRun = true;
        try
        {
            var run = await store.GetRunAsync(runId, cancellationToken)
                ?? throw new InvalidOperationException("Automation run not found.");

            // A run that is not Queued is already owned by something else (the scheduler, or a
            // completed attempt). Report it without clobbering that owner's state.
            if (run.Status != JobRunStatus.Queued)
            {
                finalizeReservedRun = false;
                throw new InvalidOperationException(
                    "Only a queued Automation run can open an interactive terminal.");
            }

            // RunNowAsync has already stamped LaunchedUTC, so the scheduler will not pick this
            // run back up. Every failure after that reservation, including the read above, must
            // finalize it here instead of leaving it Queued-but-launched for the stalled-launch
            // sweeper.
            if (run.EnvironmentId is not int environmentId)
                throw new InvalidOperationException("The Automation no longer has a Worker.");

            var environment = await repository.GetEnvironmentByIdAsync(environmentId, cancellationToken);
            if (environment is null
                || environment.LLM != run.Llm
                || !ProjectPathComparer.IsVisibleIn(environment.ProjectPath, run.ProjectPath))
            {
                throw new InvalidOperationException("The Automation Worker no longer exists in this project.");
            }

            var workingDirectory = run.ProjectPath;
            if (environment.UsesWorkspaceClone)
            {
                var workspaceService = scope.ServiceProvider.GetRequiredService<IRunWorkspaceService>();
                var workspace = await workspaceService.ResolveAsync(
                    environment,
                    run.ProjectPath,
                    cancellationToken);
                if (!workspace.Success)
                    throw new InvalidOperationException(workspace.Error);

                workingDirectory = workspace.WorkingDirectory;
            }

            try
            {
                await repository.TouchEnvironmentLastUsedAsync(environment.Id, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(
                    ex,
                    "[TerminalTabs] Could not update LastUsedUTC for Automation Worker {EnvironmentId}",
                    environment.Id);
            }

            var childArguments = BuildAutomationChildArguments(
                run,
                environment,
                workingDirectory,
                Environment.ProcessId);
            return await CreateTabCoreAsync(
                childArguments,
                run.Id,
                waitForActiveSession: true,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (finalizeReservedRun)
            {
                await store.CompleteRunAsync(
                    runId,
                    JobRunStatus.Cancelled,
                    JobRunOutcome.ToExitCode(JobRunStatus.Cancelled),
                    JobRunOutcome.CancelledMessage,
                    CancellationToken.None);
            }
            throw;
        }
        catch (Exception ex)
        {
            if (finalizeReservedRun)
            {
                await store.CompleteRunAsync(
                    runId,
                    JobRunStatus.Failed,
                    null,
                    $"Could not open an interactive terminal for this Automation: {ex.Message}",
                    CancellationToken.None);
            }
            throw;
        }
    }

    internal static string[] BuildAutomationChildArguments(
        JobRunRecord run,
        LLM_Environment environment,
        string workingDirectory,
        int parentProcessId)
    {
        var vbArgs = JobLaunchService.BuildVbArgs(run).ToList();
        vbArgs.AddRange(
        [
            "--env-id", environment.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--vs-code-v1",
            "--parent-pid", parentProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ]);

        var cliArgs = string.IsNullOrWhiteSpace(environment.CustomArgs)
            ? []
            : ShellArgSanitizer.ParseAndValidate(environment.CustomArgs);

        return BaseLlmCliLauncher.BuildVbArgv(
            run.Llm,
            workingDirectory,
            cliArgs,
            environment.CustomName,
            vbArgs.ToArray());
    }

    private async Task<TerminalTabStatusResponse> CreateTabCoreAsync(
        IReadOnlyList<string>? childArguments,
        string? automationRunId,
        bool waitForActiveSession,
        CancellationToken cancellationToken)
    {
        await _createGate.WaitAsync(cancellationToken);
        var gateHeld = true;

        TerminalChildProcess? child = null;
        try
        {
            lock (_lock)
            {
                if (_tabs.Count >= MaxTabs)
                {
                    throw new InvalidOperationException($"Maximum of {MaxTabs} terminal tabs reached.");
                }
            }

            child = await SpawnChildAsync(childArguments, automationRunId, cancellationToken);

            CancellationToken relayToken;
            lock (_lock)
            {
                _tabs[child.TabId] = child;
                var relayCts = new CancellationTokenSource();
                _tabRelayCts[child.TabId] = relayCts;
                // Read the token here, not after the lock. DeleteTabAsync removes and disposes
                // this CTS under the same lock, so a later lookup can miss the entry or touch a
                // disposed source if the tab is closed while this method is still setting up.
                relayToken = relayCts.Token;
                EnsureTabsOwnerLocked();
            }

            // A Worker's pre-launch steps can run for minutes before its PTY becomes active. Tab
            // capacity is reserved now, so no reason remains to serialize every other new-tab
            // request behind that wait.
            _createGate.Release();
            gateHeld = false;

            _ = RelayChildAppEventsAsync(child, relayToken);
            if (waitForActiveSession)
                return await WaitForActiveSessionAsync(child, cancellationToken);

            return await BuildTabStatusAsync(child, cancellationToken);
        }
        catch
        {
            if (child != null)
            {
                await TerminateChildAsync(child, stopSessionFirst: false);
            }
            throw;
        }
        finally
        {
            if (gateHeld)
                _createGate.Release();
        }
    }

    /// <summary>
    /// Polls the child until its PTY reports an active session, with a bounded deadline and a
    /// backoff. Each probe is a real HTTP round-trip to the child, so the interval widens from
    /// <see cref="ActiveSessionPollMin"/> to <see cref="ActiveSessionPollMax"/> rather than
    /// hammering a Worker whose pre-launch steps are still running.
    /// </summary>
    private async Task<TerminalTabStatusResponse> WaitForActiveSessionAsync(
        TerminalChildProcess child,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ActiveSessionTimeout;
        var delay = ActiveSessionPollMin;

        while (!cancellationToken.IsCancellationRequested && !child.Process.HasExited)
        {
            var status = await GetTerminalStatusFromChildAsync(child, cancellationToken);
            if (status?.HasActiveSession == true)
            {
                return new TerminalTabStatusResponse(
                    child.TabId,
                    child.CreatedUtc,
                    true,
                    status.SessionId,
                    status.Cli,
                    status.WorkingDirectory);
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new InvalidOperationException(
                    $"The Automation terminal did not become ready within {ActiveSessionTimeout.TotalMinutes:0} minutes.");
            }

            await Task.Delay(delay, cancellationToken);
            delay = delay < ActiveSessionPollMax
                ? TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, ActiveSessionPollMax.Ticks))
                : ActiveSessionPollMax;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("The Automation terminal exited before its TUI was ready.");
    }

    public async Task<bool> DeleteTabAsync(string tabId, CancellationToken cancellationToken = default)
    {
        TerminalChildProcess? child;
        lock (_lock)
        {
            if (!_tabs.TryGetValue(tabId, out child))
            {
                return false;
            }
            _tabs.Remove(tabId);
            if (_tabRelayCts.Remove(tabId, out var relayCts))
            {
                relayCts.Cancel();
                relayCts.Dispose();
            }
            ReleaseTabsOwnerIfNeededLocked();
        }

        await TerminateChildAsync(child, stopSessionFirst: true);
        return true;
    }

    public async Task<TerminalStatusResponse?> GetStatusAsync(string tabId, CancellationToken cancellationToken = default)
    {
        var child = GetChildOrNull(tabId);
        if (child == null)
        {
            return null;
        }

        if (child.Process.HasExited)
        {
            RemoveChild(tabId, child.Process.Id);
            return null;
        }

        return await GetTerminalStatusFromChildAsync(child, cancellationToken);
    }

    public async Task<TerminalStatusResponse> StartSessionAsync(string tabId, StartTerminalRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Cli))
        {
            throw new InvalidOperationException("CLI type is required.");
        }

        // Resolve the tab BEFORE provisioning. Workspace resolution can clone a whole
        // repository, and doing that for a tab id that turns out not to exist would burn
        // minutes and disk on a request that was always going to 404.
        var child = GetChildOrThrow(tabId);

        // Resolved here rather than in the browser: the frontend always sends the project root,
        // and an environment's workspace mode is server state. Doing the swap at this seam keeps
        // in-app tabs consistent with external launches without the client knowing about clones.
        request = await ApplyWorkspaceAsync(request, cancellationToken);

        return await SendTerminalStatusRequestAsync(
            child,
            HttpMethod.Post,
            "/api/v1/terminal/start",
            request,
            cancellationToken,
            // HttpClient's default 100 s would fail a tab start whose Environment Steps ran longer
            // than that, even though the steps themselves succeeded. The real bound belongs to the
            // work being waited on: each step carries its own TimeoutSeconds, and the browser's
            // RequestAborted still cancels the whole thing.
            timeout: System.Threading.Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// Redirects a start request into the environment's workspace when it has one. Returns the
    /// request unchanged for a plain CLI, an unknown environment, or Project mode — the common
    /// case pays one environment lookup and nothing else.
    /// </summary>
    private async Task<StartTerminalRequest> ApplyWorkspaceAsync(
        StartTerminalRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.EnvironmentName))
            return request;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var llm = scope.ServiceProvider.GetRequiredService<ILlmParser>().Parse(request.Cli);
        if (llm == LLM.NotSet || llm == LLM.Shell)
            return request;

        var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
        var environment = await repository.GetEnvironmentByNameAndLlmAsync(
            request.EnvironmentName,
            llm,
            cancellationToken);

        if (environment is null || !environment.UsesWorkspaceClone)
            return request;

        var projectPath = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? _launchDirectory
            : request.WorkingDirectory;

        var workspaceService = scope.ServiceProvider.GetRequiredService<IRunWorkspaceService>();
        var workspace = await workspaceService.ResolveAsync(environment, projectPath, cancellationToken);

        // The route turns this into a 400 carrying the reason, which is what the user needs to
        // see — starting the tab in the project directory instead would silently write the
        // agent's changes to the very tree the workspace exists to protect.
        if (!workspace.Success)
            throw new InvalidOperationException(workspace.Error);

        return request with { WorkingDirectory = workspace.WorkingDirectory };
    }

    public async Task<TerminalStatusResponse> StopSessionAsync(string tabId, CancellationToken cancellationToken = default)
    {
        var child = GetChildOrThrow(tabId);
        return await SendTerminalStatusRequestAsync(
            child,
            HttpMethod.Post,
            "/api/v1/terminal/stop",
            payload: null,
            cancellationToken);
    }

    public async Task<TerminalInputResponse> SendInputAsync(string tabId, TerminalInputRequest request, CancellationToken cancellationToken = default)
    {
        if (request == null || string.IsNullOrEmpty(request.Text))
        {
            throw new InvalidOperationException("Input text is required.");
        }

        var child = GetChildOrThrow(tabId);
        var response = await SendTerminalInputRequestAsync(
            child,
            HttpMethod.Post,
            "/api/v1/terminal/input",
            request,
            cancellationToken);

        return response with { TabId = tabId };
    }

    public async Task<TerminalSnapshotResponse?> CaptureSnapshotAsync(string tabId, CancellationToken cancellationToken = default)
    {
        var child = GetChildOrThrow(tabId);
        var response = await SendTerminalSnapshotRequestAsync(
            child,
            HttpMethod.Get,
            "/api/v1/terminal/snapshot",
            cancellationToken);

        return response == null ? null : response with { TabId = tabId };
    }

    public async Task HandleWebSocketProxyAsync(string tabId, WebSocket browserSocket, int? cols = null, int? rows = null, CancellationToken cancellationToken = default)
    {
        var child = GetChildOrThrow(tabId);
        var pid = -1;
        try { pid = child.Process.Id; } catch { }

        Log.Information(
            "[TerminalTabs] WS proxy opening. tabId={TabId} pid={Pid} port={Port} cols={Cols} rows={Rows}",
            tabId, pid, child.Port, cols, rows);

        using var upstream = new ClientWebSocket();
        upstream.Options.AddSubProtocol(child.SessionToken);
        upstream.Options.AddSubProtocol(child.TabToken);

        // Forward client dimensions so the child process can resize the PTY
        // before sending the replay buffer, preventing double-draw on reconnect.
        var query = (cols.HasValue && rows.HasValue) ? $"?cols={cols}&rows={rows}" : string.Empty;
        var upstreamUri = new Uri($"ws://127.0.0.1:{child.Port}/api/v1/terminal/ws{query}");
        await upstream.ConnectAsync(upstreamUri, cancellationToken);
        Log.Information("[TerminalTabs] WS proxy connected to child. tabId={TabId} pid={Pid}", tabId, pid);

        var childToBrowser = RelayWebSocketAsync(upstream, browserSocket, cancellationToken);
        var browserToChild = RelayWebSocketAsync(browserSocket, upstream, cancellationToken);

        var completed = await Task.WhenAny(childToBrowser, browserToChild);
        var direction = completed == childToBrowser ? "child→browser" : "browser→child";
        Exception? relayError = null;
        try { await completed; } catch (Exception ex) { relayError = ex; }

        Log.Information(
            "[TerminalTabs] WS proxy relay completed (one side closed). tabId={TabId} pid={Pid} firstClosed={Direction} childAlive={ChildAlive} upstreamState={UpstreamState} browserState={BrowserState} error={Error}",
            tabId, pid, direction,
            !child.Process.HasExited,
            upstream.State,
            browserSocket.State,
            relayError?.GetType().Name);

        await CloseWebSocketAsync(upstream, cancellationToken);
        await CloseWebSocketAsync(browserSocket, cancellationToken);

        Log.Information("[TerminalTabs] WS proxy closed. tabId={TabId} pid={Pid} childAlive={ChildAlive}", tabId, pid, !child.Process.HasExited);
    }

    /// <summary>
    /// Relays AppEvent messages from a child process to the parent's IAppEventBus,
    /// enriching each event payload with the child's tabId.
    /// </summary>
    private async Task RelayChildAppEventsAsync(TerminalChildProcess child, CancellationToken ct)
    {
        var reconnects = 0;
        while (!ct.IsCancellationRequested && !child.Process.HasExited)
        {
            var attemptStart = DateTime.UtcNow;
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.AddSubProtocol(child.SessionToken);
                ws.Options.AddSubProtocol(child.TabToken);

                var uri = new Uri($"ws://127.0.0.1:{child.Port}/api/v1/events/ws");
                await ws.ConnectAsync(uri, ct);
                Log.Information(
                    "[TerminalTabs] AppEvent relay connected to child. tabId={TabId} pid={Pid} port={Port} reconnectAttempt={Reconnects}",
                    child.TabId, TryGetPid(child), child.Port, reconnects);

                var buffer = new byte[8192];
                using var frameAccumulator = new MemoryStream();
                while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    var result = await ws.ReceiveAsync(buffer, ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;
                    if (result.MessageType != WebSocketMessageType.Text)
                        continue;

                    frameAccumulator.Write(buffer, 0, result.Count);

                    if (!result.EndOfMessage)
                        continue;

                    var json = Encoding.UTF8.GetString(frameAccumulator.GetBuffer(), 0, (int)frameAccumulator.Length);
                    frameAccumulator.SetLength(0);

                    try
                    {
                        var appEvent = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.AppEvent);
                        if (appEvent != null)
                        {
                            var savings = appEvent.Type == ActivityEventBusExtensions.ProxyActivityEventType
                                ? await ReadAppWideSavingsAsync()
                                : null;
                            var enriched = EnrichPayload(appEvent, child.TabId, savings);
                            _appEventBus.Publish(enriched);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "[TerminalTabs] AppEvent deserialize failed. tabId={TabId} snippet={Snippet}",
                            child.TabId, json.Length > 200 ? json[..200] : json);
                    }
                }

                Log.Information(
                    "[TerminalTabs] AppEvent relay inner loop exited. tabId={TabId} pid={Pid} wsState={WsState} childAlive={ChildAlive} sessionDuration={Duration}",
                    child.TabId, TryGetPid(child), ws.State, !child.Process.HasExited, DateTime.UtcNow - attemptStart);
            }
            catch (OperationCanceledException)
            {
                Log.Information("[TerminalTabs] AppEvent relay cancelled. tabId={TabId} pid={Pid}", child.TabId, TryGetPid(child));
                return;
            }
            catch (Exception ex)
            {
                Log.Warning(ex,
                    "[TerminalTabs] AppEvent relay errored; will retry. tabId={TabId} pid={Pid} childAlive={ChildAlive} attemptDuration={Duration} reconnects={Reconnects}",
                    child.TabId, TryGetPid(child), !child.Process.HasExited, DateTime.UtcNow - attemptStart, reconnects);
            }
            reconnects++;
        }

        Log.Warning(
            "[TerminalTabs] AppEvent relay loop exited. tabId={TabId} pid={Pid} childAlive={ChildAlive} cancelled={Cancelled} reconnects={Reconnects}",
            child.TabId, TryGetPid(child), !child.Process.HasExited, ct.IsCancellationRequested, reconnects);
    }

    private static int TryGetPid(TerminalChildProcess child)
    {
        try { return child.Process.Id; } catch { return -1; }
    }

    /// <summary>
    /// Re-reads the shared savings table for the tally the browser should see. Each terminal tab
    /// proxies through its own child process and therefore tallies only its own traffic — relaying
    /// those numbers untouched is what made the meter jump between tabs and read zero for every
    /// freshly spawned one. The wait is bounded because state.db is shared with background
    /// embedding work, and a display counter must never be why this relay stops pumping events.
    /// </summary>
    private async Task<TokenSavingsTotals> ReadAppWideSavingsAsync()
    {
        // The timeout is cancelled rather than abandoned. WhenAny leaves the loser running, and the
        // refresh wins nearly every time, so an uncancelled Task.Delay would park a live timer for
        // the full 250 ms on every proxy event — one per LLM request per tab, forever.
        using var timeout = new CancellationTokenSource();
        var refresh = _tokenSavings.RefreshAsync();
        var completed = await Task.WhenAny(
            refresh, Task.Delay(TimeSpan.FromMilliseconds(250), timeout.Token));
        timeout.Cancel();

        // WhenAny does not observe the winner's exception. The refresh swallows its own failures
        // today, so this only guards the day that changes — a faulted display refresh must not
        // surface as an unobserved task exception in an unrelated part of the process.
        if (completed == refresh && refresh.IsFaulted)
        {
            Log.Warning(
                refresh.Exception,
                "[TerminalTabs] Savings refresh faulted; reporting the last known tally.");
        }

        return _tokenSavings.GetTotals();
    }

    private static AppEvent EnrichPayload(AppEvent appEvent, string tabId, TokenSavingsTotals? savings)
    {
        var node = JsonNode.Parse(appEvent.Payload.GetRawText());
        if (node is JsonObject obj)
        {
            obj["tabId"] = tabId;
            if (savings is not null)
            {
                // Names must match the camelCase wire form of ProxyActivityPingPayload.
                obj["tokensSavedTotal"] = savings.TokensSaved;
                obj["tokensSavedSession"] = savings.SessionTokensSaved;
                obj["tokensSavedMonth"] = savings.MonthTokensSaved;
            }

            var enrichedJson = obj.ToJsonString();
            using var doc = JsonDocument.Parse(enrichedJson);
            return new AppEvent(appEvent.Type, doc.RootElement.Clone());
        }

        return appEvent;
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        TerminalChildProcess[] snapshot;
        lock (_lock)
        {
            snapshot = _tabs.Values.ToArray();
            _tabs.Clear();
            foreach (var cts in _tabRelayCts.Values)
            {
                cts.Cancel();
                cts.Dispose();
            }
            _tabRelayCts.Clear();
            ReleaseTabsOwnerIfNeededLocked();
        }

        if (snapshot.Length == 0)
        {
            return;
        }

        var stopTasks = snapshot.Select(child => TerminateChildAsync(child, stopSessionFirst: true));
        await Task.WhenAll(stopTasks);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAllAsync(CancellationToken.None);
        }
        finally
        {
            ReleaseTabsOwner();
            _createGate.Dispose();
        }
    }

    private TerminalChildProcess GetChildOrThrow(string tabId)
    {
        var child = GetChildOrNull(tabId);
        if (child == null)
        {
            throw new KeyNotFoundException($"Terminal tab '{tabId}' was not found.");
        }
        return child;
    }

    private TerminalChildProcess? GetChildOrNull(string tabId)
    {
        lock (_lock)
        {
            return _tabs.TryGetValue(tabId, out var child) ? child : null;
        }
    }

    private async Task<TerminalTabStatusResponse> BuildTabStatusAsync(TerminalChildProcess child, CancellationToken cancellationToken)
    {
        TerminalStatusResponse? status = null;
        try
        {
            status = await GetTerminalStatusFromChildAsync(child, cancellationToken);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[TerminalTabs] Failed to fetch status for tab {TabId}", child.TabId);
        }

        var hasActiveSession = status?.HasActiveSession ?? false;
        var sessionId = status?.SessionId;

        return new TerminalTabStatusResponse(
            child.TabId,
            child.CreatedUtc,
            hasActiveSession,
            sessionId,
            status?.Cli,
            status?.WorkingDirectory);
    }

    private async Task<TerminalChildProcess> SpawnChildAsync(
        IReadOnlyList<string>? childArguments,
        string? automationRunId,
        CancellationToken cancellationToken)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new InvalidOperationException("Unable to determine executable path for spawning terminal tabs.");
        }

        var tabId = Guid.NewGuid().ToString("N");
        var bootstrapTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = _launchDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            },
            EnableRaisingEvents = true
        };

        var arguments = childArguments
            ?? ["--vs-code-v1", "--parent-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)];
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);

        foreach (var kvp in _toolApiContext.BuildEnvironment())
        {
            process.StartInfo.Environment[kvp.Key] = kvp.Value;
        }
        process.StartInfo.Environment[LocalToolApiContext.CurrentTabIdVariable] = tabId;

        process.OutputDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data))
            {
                return;
            }

            var line = args.Data.Trim();
            Log.Debug("[TerminalTabs:{TabId}] {Line}", tabId, line);

            if (!line.StartsWith("vs-code-v1=", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var bootstrapUrl = line["vs-code-v1=".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(bootstrapUrl))
            {
                bootstrapTcs.TrySetResult(bootstrapUrl);
            }
        };

        process.ErrorDataReceived += (_, args) =>
        {
            if (string.IsNullOrWhiteSpace(args.Data))
            {
                return;
            }

            Log.Information("[TerminalTabs:{TabId}][stderr] {Line}", tabId, args.Data.Trim());
        };

            process.Exited += (_, _) =>
            {
                int? exitCode = null;
                string? exitCodeHex = null;
                DateTime? exitTime = null;
                TimeSpan? uptime = null;
                try { exitCode = process.ExitCode; exitCodeHex = $"0x{(uint)exitCode:X8}"; } catch { }
                try { exitTime = process.ExitTime; } catch { }
                try { uptime = process.ExitTime - process.StartTime; } catch { }

                Log.Warning(
                    "[TerminalTabs] Child process exited. tabId={TabId} pid={Pid} exitCode={ExitCode} exitCodeHex={ExitCodeHex} exitTimeUtc={ExitTimeUtc} uptime={Uptime} bootstrapCompleted={BootstrapCompleted}",
                    tabId,
                    process.Id,
                    exitCode,
                    exitCodeHex,
                    exitTime?.ToUniversalTime().ToString("O"),
                    uptime,
                    bootstrapTcs.Task.IsCompleted);

                RemoveChild(tabId, process.Id);
                if (!bootstrapTcs.Task.IsCompleted)
                {
                    bootstrapTcs.TrySetException(new InvalidOperationException($"Terminal tab process exited before startup handshake ({tabId})."));
            }
        };

        Log.Information(
            "[TerminalTabs] Spawning child process. selfPid={SelfPid} exePath={ExePath} launchDir={LaunchDir}",
            Environment.ProcessId,
            exePath,
            _launchDirectory);

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start terminal tab process.");
        }

        Log.Information(
            "[TerminalTabs] Child process started. pid={Pid} selfPid={SelfPid}",
            process.Id,
            Environment.ProcessId);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            string bootstrapUrl;
            try
            {
                using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startupCts.CancelAfter(TimeSpan.FromSeconds(StartupTimeoutSeconds));
                bootstrapUrl = await bootstrapTcs.Task.WaitAsync(startupCts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("Timed out waiting for child terminal server bootstrap URL.");
            }

            if (!Uri.TryCreate(bootstrapUrl, UriKind.Absolute, out var bootstrapUri) || bootstrapUri.Port <= 0)
            {
                throw new InvalidOperationException($"Invalid bootstrap URL from child process: {bootstrapUrl}");
            }

            await WaitForHealthyChildAsync(bootstrapUri.Port, cancellationToken);
            var (sessionToken, tabToken) = await BootstrapChildAndGetTokenAsync(bootstrapUrl, cancellationToken);

            return new TerminalChildProcess(
                tabId,
                process,
                bootstrapUri.Port,
                bootstrapUrl,
                sessionToken,
                tabToken,
                DateTime.UtcNow,
                automationRunId);
        }
        catch
        {
            // Ensure failed handshakes never leave orphaned vb child processes.
            await TerminateRawProcessAsync(process);
            throw;
        }
    }

    private async Task WaitForHealthyChildAsync(int port, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(2);

        // The bare /health probe is the only unauthenticated endpoint: this poll runs BEFORE the
        // child's bootstrap code is consumed, so no token exists yet to authenticate with.
        var healthUrl = $"http://127.0.0.1:{port}/health";
        for (var attempt = 0; attempt < HealthAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var response = await http.GetAsync(healthUrl, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // Retry until timeout.
            }

            await Task.Delay(HealthDelayMs, cancellationToken);
        }

        throw new TimeoutException($"Child terminal server on port {port} did not become healthy.");
    }

    private static async Task<(string SessionToken, string TabToken)> BootstrapChildAndGetTokenAsync(string bootstrapUrl, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await http.GetAsync(bootstrapUrl, cancellationToken);

        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Found)
        {
            throw new InvalidOperationException(
                $"Child bootstrap failed with status {(int)response.StatusCode} ({response.ReasonPhrase}).");
        }

        if (!response.Headers.TryGetValues("Set-Cookie", out var cookieHeaders))
        {
            throw new InvalidOperationException("Child bootstrap response did not set a session cookie.");
        }

        string? sessionToken = null;
        foreach (var cookieHeader in cookieHeaders)
        {
            if (!cookieHeader.StartsWith("viberails_session=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var firstPart = cookieHeader.Split(';', 2)[0];
            var token = firstPart["viberails_session=".Length..].Trim().Trim('"');
            if (!string.IsNullOrWhiteSpace(token))
            {
                // ASP.NET encodes cookie values in Set-Cookie (%2F, %2B, %3D).
                // Header-based auth expects the raw token value.
                try
                {
                    sessionToken = Uri.UnescapeDataString(token);
                }
                catch
                {
                    sessionToken = token;
                }
                break;
            }
        }

        if (sessionToken == null)
            throw new InvalidOperationException("Unable to parse child session token from bootstrap response.");

        var tabToken = response.Headers.TryGetValues("viberails_tab", out var tabTokenValues)
            ? tabTokenValues.FirstOrDefault()
            : null;

        if (string.IsNullOrWhiteSpace(tabToken))
            throw new InvalidOperationException("Child bootstrap response did not include a tab token.");

        return (sessionToken, tabToken);
    }

    private async Task<TerminalStatusResponse> SendTerminalStatusRequestAsync(
        TerminalChildProcess child,
        HttpMethod method,
        string path,
        StartTerminalRequest? payload,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        if (child.Process.HasExited)
        {
            RemoveChild(child.TabId, child.Process.Id);
            throw new InvalidOperationException($"Terminal tab process has exited (tab {child.TabId[..Math.Min(8, child.TabId.Length)]}).");
        }

        using var request = CreateChildRequest(child, method, path, payload);
        var http = _httpClientFactory.CreateClient();
        if (timeout.HasValue)
        {
            // Safe on a factory client: CreateClient hands back a fresh HttpClient over a pooled
            // handler, and Timeout is only immutable once a request has been sent on it.
            http.Timeout = timeout.Value;
        }
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Could not reach terminal tab on port {child.Port}: {ex.Message}", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await ReadErrorTextAsync(response, cancellationToken);
                throw new InvalidOperationException(
                    $"Child request {path} failed ({(int)response.StatusCode}): {errorText}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var status = await JsonSerializer.DeserializeAsync(
                stream,
                AppJsonSerializerContext.Default.TerminalStatusResponse,
                cancellationToken);

            return status ?? new TerminalStatusResponse(false, null);
        }
    }

    private async Task<TerminalInputResponse> SendTerminalInputRequestAsync(
        TerminalChildProcess child,
        HttpMethod method,
        string path,
        TerminalInputRequest payload,
        CancellationToken cancellationToken)
    {
        if (child.Process.HasExited)
        {
            RemoveChild(child.TabId, child.Process.Id);
            throw new InvalidOperationException($"Terminal tab process has exited (tab {child.TabId[..Math.Min(8, child.TabId.Length)]}).");
        }

        using var request = CreateChildRequest(child, method, path, payload);
        var http = _httpClientFactory.CreateClient();
        using var response = await http.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await ReadErrorTextAsync(response, cancellationToken);
            throw new InvalidOperationException(
                $"Child request {path} failed ({(int)response.StatusCode}): {errorText}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var inputResponse = await JsonSerializer.DeserializeAsync(
            stream,
            AppJsonSerializerContext.Default.TerminalInputResponse,
            cancellationToken);

        return inputResponse ?? new TerminalInputResponse(false, "Child did not return an input response.");
    }

    private async Task<TerminalSnapshotResponse?> SendTerminalSnapshotRequestAsync(
        TerminalChildProcess child,
        HttpMethod method,
        string path,
        CancellationToken cancellationToken)
    {
        if (child.Process.HasExited)
        {
            RemoveChild(child.TabId, child.Process.Id);
            return null;
        }

        using var request = NewChildRequest(child, method, path);
        var http = _httpClientFactory.CreateClient();
        using var response = await http.SendAsync(request, cancellationToken);

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorText = await ReadErrorTextAsync(response, cancellationToken);
            throw new InvalidOperationException(
                $"Child request {path} failed ({(int)response.StatusCode}): {errorText}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync(
            stream,
            AppJsonSerializerContext.Default.TerminalSnapshotResponse,
            cancellationToken);
    }

    private async Task<TerminalStatusResponse?> GetTerminalStatusFromChildAsync(
        TerminalChildProcess child,
        CancellationToken cancellationToken)
    {
        if (child.Process.HasExited)
        {
            RemoveChild(child.TabId, child.Process.Id);
            return null;
        }

        using var request = NewChildRequest(child, HttpMethod.Get, "/api/v1/terminal/status");
        var http = _httpClientFactory.CreateClient();
        using var response = await http.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync(
            stream,
            AppJsonSerializerContext.Default.TerminalStatusResponse,
            cancellationToken);
    }

    // Header-only base. No payload overload participates in its resolution, so — unlike the old
    // pattern where a typed overload reached the base by passing a (StartTerminalRequest?)null cast —
    // no overload can accidentally recurse into itself if that argument is ever changed.
    private static HttpRequestMessage NewChildRequest(
        TerminalChildProcess child,
        HttpMethod method,
        string path)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{child.Port}{path}");
        request.Headers.TryAddWithoutValidation("viberails_session", child.SessionToken);
        request.Headers.TryAddWithoutValidation("viberails_tab", child.TabToken);
        return request;
    }

    private static HttpRequestMessage CreateChildRequest(
        TerminalChildProcess child,
        HttpMethod method,
        string path,
        StartTerminalRequest? payload)
    {
        var request = NewChildRequest(child, method, path);
        if (payload != null)
        {
            var json = JsonSerializer.Serialize(payload, AppJsonSerializerContext.Default.StartTerminalRequest);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static HttpRequestMessage CreateChildRequest(
        TerminalChildProcess child,
        HttpMethod method,
        string path,
        TerminalInputRequest payload)
    {
        var request = NewChildRequest(child, method, path);
        var json = JsonSerializer.Serialize(payload, AppJsonSerializerContext.Default.TerminalInputRequest);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    private static async Task<string> ReadErrorTextAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
            {
                return response.ReasonPhrase ?? "Unknown error";
            }

            try
            {
                var parsed = JsonSerializer.Deserialize(content, AppJsonSerializerContext.Default.ErrorResponse);
                if (!string.IsNullOrWhiteSpace(parsed?.Error))
                {
                    return parsed.Error;
                }
            }
            catch
            {
                // Not JSON, return raw content below.
            }

            return content;
        }
        catch
        {
            return response.ReasonPhrase ?? "Unknown error";
        }
    }

    /// <summary>
    /// Tears a tab's child process down.
    ///
    /// Deliberately takes no <see cref="CancellationToken"/>. Every wait below is already bounded
    /// by an explicit timeout, so a token could only ever cut teardown short — and the callers hold
    /// the aborting request's own token. Threading it through meant a browser that cancelled its
    /// DELETE skipped the graceful-stop window immediately below, turning a deliberate Automation
    /// stop into an Interrupted run: the exact outcome that window exists to prevent.
    /// </summary>
    private async Task TerminateChildAsync(TerminalChildProcess child, bool stopSessionFirst)
    {
        var pid = -1;
        try { pid = child.Process.Id; } catch { }

        Log.Warning(
            "[TerminalTabs] TerminateChildAsync invoked. tabId={TabId} pid={Pid} stopSessionFirst={StopSessionFirst} alreadyExited={AlreadyExited} caller={Caller}",
            child.TabId,
            pid,
            stopSessionFirst,
            child.Process.HasExited,
            new System.Diagnostics.StackTrace(1, fNeedFileInfo: false).ToString());

        if (stopSessionFirst && !child.Process.HasExited)
        {
            var stopRequested = false;
            try
            {
                Log.Information("[TerminalTabs] Sending graceful /terminal/stop before kill. tabId={TabId} pid={Pid}", child.TabId, pid);
                await SendTerminalStatusRequestAsync(
                    child,
                    HttpMethod.Post,
                    "/api/v1/terminal/stop",
                    payload: null,
                    CancellationToken.None,
                    timeout: GracefulStopRequestTimeout);
                stopRequested = true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[TerminalTabs] Graceful stop request failed (best-effort). tabId={TabId} pid={Pid}", child.TabId, pid);
            }

            // An Automation child owns its normal cancellation path: /terminal/stop sets the
            // durable cancel flag and JobRunner's watcher records Cancelled before terminating its
            // process tree. Give that bounded path one poll interval before falling back to the
            // generic hard kill, or closing the tab would turn a deliberate stop into Interrupted.
            if (stopRequested
                && child.AutomationRunId is not null
                && await WaitForExitAsync(child.Process, 3500))
            {
                return;
            }
        }

        await TerminateRawProcessAsync(child.Process);
    }

    private static async Task TerminateRawProcessAsync(Process process)
    {
        var pid = -1;
        try { pid = process.Id; } catch { }

        if (process.HasExited)
        {
            Log.Information("[TerminalTabs] TerminateRawProcessAsync: process already exited. pid={Pid}", pid);
            return;
        }

        Log.Warning("[TerminalTabs] Hard-killing child process tree. pid={Pid}", pid);
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "[TerminalTabs] process.Kill(entireProcessTree) failed; falling back to taskkill. pid={Pid}", pid);
        }

        if (await WaitForExitAsync(process, 3000))
        {
            Log.Information("[TerminalTabs] Child exited after process.Kill. pid={Pid}", pid);
            return;
        }

        if (OperatingSystem.IsWindows() && process.Id > 0)
        {
            Log.Warning("[TerminalTabs] Invoking taskkill /T /F fallback. pid={Pid}", pid);
            await KillProcessTreeWindowsAsync(process.Id);
            await WaitForExitAsync(process, 3000);
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, int timeoutMs)
    {
        if (process.HasExited)
        {
            return true;
        }

        try
        {
            return await Task.Run(() => process.WaitForExit(timeoutMs));
        }
        catch
        {
            return process.HasExited;
        }
    }

    private static async Task KillProcessTreeWindowsAsync(int pid)
    {
        using var killer = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = $"/PID {pid} /T /F",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };

        try
        {
            killer.Start();
            await killer.WaitForExitAsync();
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static async Task RelayWebSocketAsync(WebSocket source, WebSocket destination, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested &&
                   source.State == WebSocketState.Open &&
                   destination.State == WebSocketState.Open)
            {
                var result = await source.ReceiveAsync(buffer, cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ForwardCloseFrameAsync(destination, result, cancellationToken);
                    break;
                }

                if (destination.State != WebSocketState.Open)
                {
                    break;
                }

                await destination.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static async Task ForwardCloseFrameAsync(
        WebSocket destination,
        WebSocketReceiveResult closeResult,
        CancellationToken cancellationToken)
    {
        if (destination.State != WebSocketState.Open && destination.State != WebSocketState.CloseReceived)
        {
            return;
        }

        var closeStatus = closeResult.CloseStatus ?? WebSocketCloseStatus.NormalClosure;
        var closeReason = SanitizeCloseReason(closeResult.CloseStatusDescription, "Peer disconnected");

        try
        {
            await destination.CloseAsync(closeStatus, closeReason, cancellationToken);
        }
        catch
        {
            // Best-effort only.
        }
    }

    private static string SanitizeCloseReason(string? reason, string fallback)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return fallback;
        }

        var trimmed = reason.Trim();
        var sb = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            if (!char.IsControl(ch))
            {
                sb.Append(ch);
            }
        }

        var sanitized = sb.ToString();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return fallback;
        }

        const int maxReasonLength = 120;
        if (sanitized.Length > maxReasonLength)
        {
            sanitized = sanitized[..maxReasonLength];
        }

        return sanitized;
    }

    private static async Task CloseWebSocketAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open && socket.State != WebSocketState.CloseReceived)
        {
            return;
        }

        try
        {
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", cancellationToken);
        }
        catch
        {
            // Best-effort only.
        }
    }

    private void RemoveChild(string tabId, int processId)
    {
        CancellationTokenSource? relayCts = null;
        var removed = false;
        var remainingTabs = 0;
        lock (_lock)
        {
            if (_tabs.TryGetValue(tabId, out var child) && child.Process.Id == processId)
            {
                _tabs.Remove(tabId);
                _tabRelayCts.Remove(tabId, out relayCts);
                ReleaseTabsOwnerIfNeededLocked();
                removed = true;
            }
            remainingTabs = _tabs.Count;
        }

        Log.Information(
            "[TerminalTabs] RemoveChild. tabId={TabId} pid={Pid} removed={Removed} remainingTabs={RemainingTabs}",
            tabId,
            processId,
            removed,
            remainingTabs);

        if (relayCts != null)
        {
            relayCts.Cancel();
            relayCts.Dispose();
        }
    }

    private void EnsureTabsOwnerLocked()
    {
        if (_tabsOwnerAcquired)
            return;

        _tabsOwnerAcquired = true;
        _localClientTracker.AcquireOwner(_tabsOwnerId);
    }

    private void ReleaseTabsOwnerIfNeededLocked()
    {
        if (!_tabsOwnerAcquired || _tabs.Count > 0)
            return;

        _tabsOwnerAcquired = false;
        _localClientTracker.ReleaseOwner(_tabsOwnerId);
    }

    private void ReleaseTabsOwner()
    {
        var shouldRelease = false;
        lock (_lock)
        {
            if (_tabsOwnerAcquired)
            {
                _tabsOwnerAcquired = false;
                shouldRelease = true;
            }
        }

        if (shouldRelease)
        {
            _localClientTracker.ReleaseOwner(_tabsOwnerId);
        }
    }
}
