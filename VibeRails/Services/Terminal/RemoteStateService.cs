using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Serilog;
using VibeRails.Utils;

namespace VibeRails.Services.Terminal;

/// <summary>
/// Handles remote registration/deregistration of terminal sessions with the VibeRails-Front server.
/// Fire-and-forget operations that don't block terminal startup/shutdown.
/// </summary>
public interface IRemoteStateService
{
    Task RegisterTerminalAsync(string sessionId, string cli, string workingDirectory, string? environmentName, string? title = null);
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
    string HostUrl
);

public record DeregisterTerminalRequest(string SessionId);

public class RemoteStateService : IRemoteStateService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public RemoteStateService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task RegisterTerminalAsync(string sessionId, string cli, string workingDirectory, string? environmentName, string? title = null)
    {
        var apiKey = ParserConfigs.GetApiKey();
        var frontendUrl = ParserConfigs.GetFrontendUrl();

        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(frontendUrl))
            return;


        var request = new HttpRequestMessage(HttpMethod.Post, $"{frontendUrl.TrimEnd('/')}/api/v1/terminal");
        request.Headers.Add("X-Api-Key", apiKey);

        var hostUrl = GetHostUrl();

        var payload = new RegisterTerminalRequest(
            sessionId,
            cli,
            workingDirectory,
            environmentName,
            title,
            hostUrl
        );

        var json = JsonSerializer.Serialize(payload, RemoteStateJsonContext.Default.RegisterTerminalRequest);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");

        try
        {
            await _httpClient.SendAsync(request);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[Remote] Failed to register terminal");
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
