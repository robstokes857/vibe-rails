using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;
using VibeRails.DTOs;
using VibeRails.Interfaces;

namespace VibeRails.Services.Terminal;

public sealed class TerminalTabHostService : ITerminalTabHostService, IAsyncDisposable
{
    private const int StartupTimeoutSeconds = 30;
    private const int HealthAttempts = 30;
    private const int HealthDelayMs = 500;

    private sealed record TerminalChildProcess(
        string TabId,
        Process Process,
        int Port,
        string BootstrapUrl,
        string SessionToken,
        string TabToken,
        DateTime CreatedUtc);

    // Cached once at startup to avoid repeated Process.GetCurrentProcess() calls per tab spawn.
    private static readonly long _selfStartTimeTicks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILocalClientTracker _localClientTracker;
    private readonly IAppEventBus _appEventBus;
#if DEBUG
    private readonly DebugEventBus _debugEventBus;
#endif
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
        IAppEventBus appEventBus
#if DEBUG
        , DebugEventBus debugEventBus
#endif
    )
    {
        _httpClientFactory = httpClientFactory;
        _localClientTracker = localClientTracker;
        _appEventBus = appEventBus;
#if DEBUG
        _debugEventBus = debugEventBus;
#endif
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
    {
        await _createGate.WaitAsync(cancellationToken);

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

            child = await SpawnChildAsync(cancellationToken);

            lock (_lock)
            {
                _tabs[child.TabId] = child;
                var relayCts = new CancellationTokenSource();
                _tabRelayCts[child.TabId] = relayCts;
                EnsureTabsOwnerLocked();
            }

            var relayToken = _tabRelayCts[child.TabId].Token;
            _ = RelayChildAppEventsAsync(child, relayToken);
#if DEBUG
            _ = RelayChildDebugEventsAsync(child, relayToken);
#endif
            return await BuildTabStatusAsync(child, cancellationToken);
        }
        catch
        {
            if (child != null)
            {
                await TerminateChildAsync(child, cancellationToken, stopSessionFirst: false);
            }
            throw;
        }
        finally
        {
            _createGate.Release();
        }
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

        await TerminateChildAsync(child, cancellationToken, stopSessionFirst: true);
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

        var child = GetChildOrThrow(tabId);
        return await SendTerminalStatusRequestAsync(
            child,
            HttpMethod.Post,
            "/api/v1/terminal/start",
            request,
            cancellationToken);
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

    public async Task HandleWebSocketProxyAsync(string tabId, WebSocket browserSocket, int? cols = null, int? rows = null, CancellationToken cancellationToken = default)
    {
        var child = GetChildOrThrow(tabId);
        using var upstream = new ClientWebSocket();
        upstream.Options.AddSubProtocol(child.SessionToken);
        upstream.Options.AddSubProtocol(child.TabToken);

        // Forward client dimensions so the child process can resize the PTY
        // before sending the replay buffer, preventing double-draw on reconnect.
        var query = (cols.HasValue && rows.HasValue) ? $"?cols={cols}&rows={rows}" : string.Empty;
        var upstreamUri = new Uri($"ws://127.0.0.1:{child.Port}/api/v1/terminal/ws{query}");
        await upstream.ConnectAsync(upstreamUri, cancellationToken);

        var childToBrowser = RelayWebSocketAsync(upstream, browserSocket, cancellationToken);
        var browserToChild = RelayWebSocketAsync(browserSocket, upstream, cancellationToken);

        await Task.WhenAny(childToBrowser, browserToChild);

        await CloseWebSocketAsync(upstream, cancellationToken);
        await CloseWebSocketAsync(browserSocket, cancellationToken);
    }

    /// <summary>
    /// Relays AppEvent messages from a child process to the parent's IAppEventBus,
    /// enriching each event payload with the child's tabId.
    /// </summary>
    private async Task RelayChildAppEventsAsync(TerminalChildProcess child, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !child.Process.HasExited)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.AddSubProtocol(child.SessionToken);
                ws.Options.AddSubProtocol(child.TabToken);

                var uri = new Uri($"ws://127.0.0.1:{child.Port}/api/v1/events/ws");
                await ws.ConnectAsync(uri, ct);

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
                            var enriched = EnrichPayloadWithTabId(appEvent, child.TabId);
                            _appEventBus.Publish(enriched);
                        }
                    }
                    catch (Exception) { /* skip malformed messages, keep relaying */ }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex) { }            
        }
    }

    private static AppEvent EnrichPayloadWithTabId(AppEvent appEvent, string tabId)
    {
        var node = JsonNode.Parse(appEvent.Payload.GetRawText());
        if (node is JsonObject obj)
        {
            obj["tabId"] = tabId;
            var enrichedJson = obj.ToJsonString();
            using var doc = JsonDocument.Parse(enrichedJson);
            return new AppEvent(appEvent.Type, doc.RootElement.Clone());
        }

        return appEvent;
    }

#if DEBUG
    private async Task RelayChildDebugEventsAsync(TerminalChildProcess child, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !child.Process.HasExited)
        {
            try
            {
                using var ws = new ClientWebSocket();
                ws.Options.AddSubProtocol(child.SessionToken);
                ws.Options.AddSubProtocol(child.TabToken);

                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{child.Port}/tooling/events/ws"), ct);

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
                        var msg = JsonSerializer.Deserialize(json, AppJsonSerializerContext.Default.EventMessage);
                        if (msg != null)
                            _debugEventBus.Publish(msg.Type, msg.Text, child.TabId);
                    }
                    catch (Exception) { /* skip malformed messages, keep relaying */ }
                }
            }
            catch (OperationCanceledException) { return; }
            catch
            {
                try { await Task.Delay(2000, ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    public async Task HandleEventWebSocketProxyAsync(string tabId, WebSocket browserSocket, CancellationToken cancellationToken = default)
    {
        var child = GetChildOrThrow(tabId);
        using var upstream = new ClientWebSocket();
        upstream.Options.AddSubProtocol(child.SessionToken);
        upstream.Options.AddSubProtocol(child.TabToken);

        var upstreamUri = new Uri($"ws://127.0.0.1:{child.Port}/tooling/events/ws");
        await upstream.ConnectAsync(upstreamUri, cancellationToken);

        var childToBrowser = RelayWebSocketAsync(upstream, browserSocket, cancellationToken);
        var browserToChild = RelayWebSocketAsync(browserSocket, upstream, cancellationToken);

        await Task.WhenAny(childToBrowser, browserToChild);

        await CloseWebSocketAsync(upstream, cancellationToken);
        await CloseWebSocketAsync(browserSocket, cancellationToken);
    }
#else
    public Task HandleEventWebSocketProxyAsync(string tabId, WebSocket browserSocket, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("Event WebSocket proxying is only available in debug builds.");
    }
#endif

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

        var stopTasks = snapshot.Select(child => TerminateChildAsync(child, cancellationToken, stopSessionFirst: true));
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
            sessionId);
    }

    private async Task<TerminalChildProcess> SpawnChildAsync(CancellationToken cancellationToken)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            throw new InvalidOperationException("Unable to determine executable path for spawning terminal tabs.");
        }

        var tabId = string.Empty;
        var bootstrapTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"--vs-code-v1 --parent-pid {Environment.ProcessId} --parent-start-ticks {_selfStartTimeTicks}",
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
                RemoveChild(tabId, process.Id);
                if (!bootstrapTcs.Task.IsCompleted)
                {
                    bootstrapTcs.TrySetException(new InvalidOperationException($"Terminal tab process exited before startup handshake ({tabId})."));
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start terminal tab process.");
        }

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

            tabId = Guid.NewGuid().ToString("N");
            await WaitForHealthyChildAsync(bootstrapUri.Port, cancellationToken);
            var (sessionToken, tabToken) = await BootstrapChildAndGetTokenAsync(bootstrapUrl, cancellationToken);

            return new TerminalChildProcess(
                tabId,
                process,
                bootstrapUri.Port,
                bootstrapUrl,
                sessionToken,
                tabToken,
                DateTime.UtcNow);
        }
        catch
        {
            // Ensure failed handshakes never leave orphaned vb child processes.
            await TerminateRawProcessAsync(process, CancellationToken.None);
            throw;
        }
    }

    private async Task WaitForHealthyChildAsync(int port, CancellationToken cancellationToken)
    {
        var http = _httpClientFactory.CreateClient();
        http.Timeout = TimeSpan.FromSeconds(2);

        var healthUrl = $"http://127.0.0.1:{port}/api/v1/context";
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
        CancellationToken cancellationToken)
    {
        if (child.Process.HasExited)
        {
            RemoveChild(child.TabId, child.Process.Id);
            throw new InvalidOperationException($"Terminal tab process has exited (tab {child.TabId[..Math.Min(8, child.TabId.Length)]}).");
        }

        using var request = CreateChildRequest(child, method, path, payload);
        var http = _httpClientFactory.CreateClient();
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

    private async Task<TerminalStatusResponse?> GetTerminalStatusFromChildAsync(
        TerminalChildProcess child,
        CancellationToken cancellationToken)
    {
        if (child.Process.HasExited)
        {
            RemoveChild(child.TabId, child.Process.Id);
            return null;
        }

        using var request = CreateChildRequest(child, HttpMethod.Get, "/api/v1/terminal/status", payload: null);
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

    private static HttpRequestMessage CreateChildRequest(
        TerminalChildProcess child,
        HttpMethod method,
        string path,
        StartTerminalRequest? payload)
    {
        var request = new HttpRequestMessage(method, $"http://127.0.0.1:{child.Port}{path}");
        request.Headers.TryAddWithoutValidation("viberails_session", child.SessionToken);
        request.Headers.TryAddWithoutValidation("viberails_tab", child.TabToken);

        if (payload != null)
        {
            var json = JsonSerializer.Serialize(payload, AppJsonSerializerContext.Default.StartTerminalRequest);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

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

    private async Task TerminateChildAsync(TerminalChildProcess child, CancellationToken cancellationToken, bool stopSessionFirst)
    {
        if (stopSessionFirst && !child.Process.HasExited)
        {
            try
            {
                await SendTerminalStatusRequestAsync(
                    child,
                    HttpMethod.Post,
                    "/api/v1/terminal/stop",
                    payload: null,
                    cancellationToken);
            }
            catch
            {
                // Best-effort.
            }
        }

        await TerminateRawProcessAsync(child.Process, cancellationToken);
    }

    private static async Task TerminateRawProcessAsync(Process process, CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            return;
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Fallback handled below.
        }

        if (await WaitForExitAsync(process, 3000, cancellationToken))
        {
            return;
        }

        if (OperatingSystem.IsWindows() && process.Id > 0)
        {
            await KillProcessTreeWindowsAsync(process.Id, cancellationToken);
            await WaitForExitAsync(process, 3000, cancellationToken);
        }
    }

    private static async Task<bool> WaitForExitAsync(Process process, int timeoutMs, CancellationToken cancellationToken)
    {
        if (process.HasExited)
        {
            return true;
        }

        try
        {
            return await Task.Run(() => process.WaitForExit(timeoutMs), cancellationToken);
        }
        catch
        {
            return process.HasExited;
        }
    }

    private static async Task KillProcessTreeWindowsAsync(int pid, CancellationToken cancellationToken)
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
            await killer.WaitForExitAsync(cancellationToken);
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
        lock (_lock)
        {
            if (_tabs.TryGetValue(tabId, out var child) && child.Process.Id == processId)
            {
                _tabs.Remove(tabId);
                _tabRelayCts.Remove(tabId, out relayCts);
                ReleaseTabsOwnerIfNeededLocked();
            }
        }

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
