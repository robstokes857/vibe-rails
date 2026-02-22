using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Serilog;
using VibeRails.DTOs;

namespace VibeRails.Services.Terminal;

public interface ITerminalTabHostService
{
    int MaxTabs { get; }
    Task<IReadOnlyList<TerminalTabStatusResponse>> ListTabsAsync(CancellationToken cancellationToken = default);
    Task<TerminalTabStatusResponse> CreateTabAsync(CancellationToken cancellationToken = default);
    Task<bool> DeleteTabAsync(string tabId, CancellationToken cancellationToken = default);
    Task<TerminalStatusResponse?> GetStatusAsync(string tabId, CancellationToken cancellationToken = default);
    Task<TerminalStatusResponse> StartSessionAsync(string tabId, StartTerminalRequest request, CancellationToken cancellationToken = default);
    Task<TerminalStatusResponse> StopSessionAsync(string tabId, CancellationToken cancellationToken = default);
    Task HandleWebSocketProxyAsync(string tabId, WebSocket browserSocket, CancellationToken cancellationToken = default);
    Task StopAllAsync(CancellationToken cancellationToken = default);
}

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
        DateTime CreatedUtc);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SemaphoreSlim _createGate = new(1, 1);
    private readonly Lock _lock = new();
    private readonly Dictionary<string, TerminalChildProcess> _tabs = new(StringComparer.Ordinal);
    private readonly string _launchDirectory;

    public int MaxTabs => 8;

    public TerminalTabHostService(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
        _launchDirectory = Directory.GetCurrentDirectory();
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
            }

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

    public async Task HandleWebSocketProxyAsync(string tabId, WebSocket browserSocket, CancellationToken cancellationToken = default)
    {
        var child = GetChildOrThrow(tabId);
        using var upstream = new ClientWebSocket();
        upstream.Options.SetRequestHeader("viberails_session", child.SessionToken);

        var upstreamUri = new Uri($"ws://127.0.0.1:{child.Port}/api/v1/terminal/ws");
        await upstream.ConnectAsync(upstreamUri, cancellationToken);

        var childToBrowser = RelayWebSocketAsync(upstream, browserSocket, cancellationToken);
        var browserToChild = RelayWebSocketAsync(browserSocket, upstream, cancellationToken);

        await Task.WhenAny(childToBrowser, browserToChild);

        await CloseWebSocketAsync(upstream, cancellationToken);
        await CloseWebSocketAsync(browserSocket, cancellationToken);
    }

    public async Task StopAllAsync(CancellationToken cancellationToken = default)
    {
        TerminalChildProcess[] snapshot;
        lock (_lock)
        {
            snapshot = _tabs.Values.ToArray();
            _tabs.Clear();
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
                Arguments = $"--vs-code-v1 --parent-pid {Environment.ProcessId}",
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

            await WaitForHealthyChildAsync(bootstrapUri.Port, cancellationToken);
            var sessionToken = await BootstrapChildAndGetTokenAsync(bootstrapUrl, cancellationToken);

            tabId = bootstrapUri.Port.ToString(CultureInfo.InvariantCulture);

            return new TerminalChildProcess(
                tabId,
                process,
                bootstrapUri.Port,
                bootstrapUrl,
                sessionToken,
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

        var healthUrl = $"http://127.0.0.1:{port}/api/v1/IsLocal";
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

    private static async Task<string> BootstrapChildAndGetTokenAsync(string bootstrapUrl, CancellationToken cancellationToken)
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
                    return Uri.UnescapeDataString(token);
                }
                catch
                {
                    return token;
                }
            }
        }

        throw new InvalidOperationException("Unable to parse child session token from bootstrap response.");
    }

    private async Task<TerminalStatusResponse> SendTerminalStatusRequestAsync(
        TerminalChildProcess child,
        HttpMethod method,
        string path,
        StartTerminalRequest? payload,
        CancellationToken cancellationToken)
    {
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
        var status = await JsonSerializer.DeserializeAsync(
            stream,
            AppJsonSerializerContext.Default.TerminalStatusResponse,
            cancellationToken);

        return status ?? new TerminalStatusResponse(false, null);
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
        lock (_lock)
        {
            if (_tabs.TryGetValue(tabId, out var child) && child.Process.Id == processId)
            {
                _tabs.Remove(tabId);
            }
        }
    }
}
