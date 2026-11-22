using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;
using VibeRails.Utils;
using VibeRails.Services.Board.Sync;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>
/// Handles remote registration/deregistration of terminal sessions with the VibeRails-Front server.
/// Fire-and-forget operations that don't block terminal startup/shutdown.
/// </summary>
public interface IRemoteStateService
{
    /// <summary>
    /// Registers (or re-registers: the server upserts by session id) a terminal with the relay.
    /// Never throws for transport or HTTP failures; returns true only when the relay accepted it.
    /// </summary>
    /// <param name="consoleHost">
    /// "bundled" for a terminal on the console host shipped with VibeRails for Windows, where this
    /// desktop answers terminal queries; null for every other terminal. The relay hands it to the
    /// remote viewer, which then answers none (VIBE-171). Every registration of a session has to
    /// carry the same value: the relay replaces the whole row.
    /// </param>
    Task<bool> RegisterTerminalAsync(string sessionId, string cli, string workingDirectory, string? environmentName,
        string? title = null, string? consoleHost = null, CancellationToken ct = default);
    Task DeregisterTerminalAsync(string sessionId);
}

// AOT-compatible JSON serialization context
[JsonSerializable(typeof(RegisterTerminalRequest))]
[JsonSerializable(typeof(DeregisterTerminalRequest))]
internal partial class RemoteStateJsonContext : JsonSerializerContext
{
}

public record RegisterTerminalRequest(
    string SessionId,
    string Cli,
    string WorkingDirectory,
    string? EnvironmentName,
    string? Title,
    string HostUrl,
    // Left out when null, so the registration of every terminal that is not on the bundled
    // console host is byte for byte what it was before this member existed (VIBE-171).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ConsoleHost = null
);

public record DeregisterTerminalRequest(string SessionId);

public class RemoteStateService : IRemoteStateService
{
    // A registration is one small POST; bound it so a stalled host cannot hold a reconnect attempt.
    private static readonly TimeSpan RegisterTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IAccountEntitlements? _entitlements;
    // One line per pause, shared by every terminal in this process; cleared by the next accepted registration.
    private static int s_proPauseLogged;

    public RemoteStateService(HttpClient httpClient, IConfiguration configuration, IAccountEntitlements? entitlements = null)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _entitlements = entitlements;
    }

    public async Task<bool> RegisterTerminalAsync(string sessionId, string cli, string workingDirectory, string? environmentName,
        string? title = null, string? consoleHost = null, CancellationToken ct = default)
    {
        var apiKey = ParserConfigs.GetApiKey();
        var frontendUrl = ParserConfigs.GetFrontendUrl();

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(frontendUrl))
            return false;


        var request = new HttpRequestMessage(HttpMethod.Post, $"{frontendUrl.TrimEnd('/')}/api/v1/terminal");
        request.Headers.Add("X-Api-Key", apiKey);

        var hostUrl = GetHostUrl();

        var payload = new RegisterTerminalRequest(
            sessionId,
            cli,
            workingDirectory,
            environmentName,
            title,
            hostUrl,
            consoleHost
        );

        var json = JsonSerializer.Serialize(payload, RemoteStateJsonContext.Default.RegisterTerminalRequest);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RegisterTimeout);
            var safety = new RemoteSafetyClient(() => new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }),
                () => new Uri(frontendUrl), () => apiKey, "/api/v1/terminal/remote-access");
            var generation = await safety.EnsureAsync(true, false, timeout.Token);
            request.Headers.Add("X-VibeRails-Safety-Protocol", "1");
            request.Headers.Add("X-VibeRails-Safety-Generation", generation);
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            if (response.IsSuccessStatusCode)
            {
                Volatile.Write(ref s_proPauseLogged, 0);
                return true;
            }
            // The plan's typed denial: the relay is off for this account, the local terminal is not.
            // Noted so later terminals skip the registration instead of being refused one by one.
            if (await ProDenial.ReadAsync(response, ProFeatures.RemoteTerminals, timeout.Token) is { } denial)
            {
                if (denial.AccountPays) _entitlements?.NoteDenied(denial.Feature);
                if (Interlocked.Exchange(ref s_proPauseLogged, 1) == 0)
                    Log.Information("[Remote] Remote terminals need Vibe Rails Pro; terminals keep running locally without the relay");
                return false;
            }
            Log.Warning("[Remote] Terminal registration was refused with HTTP {Status}", (int)response.StatusCode);
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Remote] Failed to register terminal");
            return false;
        }
        finally
        {
            request.Dispose();
        }
    }

    public async Task DeregisterTerminalAsync(string sessionId)
    {
        var apiKey = ParserConfigs.GetApiKey();
        var frontendUrl = ParserConfigs.GetFrontendUrl();

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(frontendUrl))
            return;

        var request = new HttpRequestMessage(HttpMethod.Delete, $"{frontendUrl.TrimEnd('/')}/api/v1/terminal");
        request.Headers.Add("X-Api-Key", apiKey);

        var payload = new DeregisterTerminalRequest(sessionId);
        var json = JsonSerializer.Serialize(payload, RemoteStateJsonContext.Default.DeregisterTerminalRequest);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            await _httpClient.SendAsync(request);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Remote] Failed to deregister terminal");
        }
    }

    private string GetHostUrl()
    {
        return _configuration["HostUrl"] ?? "https://viberails.ai";
    }
}
