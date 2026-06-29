using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using VibeRails.DTOs;

namespace VibeRails.Services.AgentTools;

public sealed class HttpAgentTerminalToolGateway : IAgentTerminalToolGateway
{
    public async Task<AgentToolTerminalListResponse> ListTerminalsAsync(CancellationToken cancellationToken = default)
    {
        return await SendAsync<object, AgentToolTerminalListResponse>(
            HttpMethod.Get,
            "/api/v1/agent-tools/terminal",
            payload: null,
            requestTypeInfo: null,
            AppJsonSerializerContext.Default.AgentToolTerminalListResponse,
            cancellationToken);
    }

    public async Task<TerminalTabStatusResponse> OpenTerminalAsync(
        AgentToolOpenTerminalRequest request,
        CancellationToken cancellationToken = default)
    {
        return await SendAsync<AgentToolOpenTerminalRequest, TerminalTabStatusResponse>(
            HttpMethod.Post,
            "/api/v1/agent-tools/terminal/open",
            request,
            AppJsonSerializerContext.Default.AgentToolOpenTerminalRequest,
            AppJsonSerializerContext.Default.TerminalTabStatusResponse,
            cancellationToken);
    }

    public async Task<TerminalInputResponse> SendInputAsync(
        string? tabId,
        TerminalInputRequest request,
        CancellationToken cancellationToken = default)
    {
        var payload = new AgentToolSendInputRequest(ResolveTabId(tabId), request.Text, request.Submit);
        return await SendAsync<AgentToolSendInputRequest, TerminalInputResponse>(
            HttpMethod.Post,
            "/api/v1/agent-tools/terminal/input",
            payload,
            AppJsonSerializerContext.Default.AgentToolSendInputRequest,
            AppJsonSerializerContext.Default.TerminalInputResponse,
            cancellationToken);
    }

    public async Task<TerminalSnapshotResponse?> CaptureSnapshotAsync(
        string? tabId,
        CancellationToken cancellationToken = default)
    {
        var payload = new AgentToolSnapshotRequest(ResolveTabId(tabId));
        return await SendAsync<AgentToolSnapshotRequest, TerminalSnapshotResponse>(
            HttpMethod.Post,
            "/api/v1/agent-tools/terminal/snapshot",
            payload,
            AppJsonSerializerContext.Default.AgentToolSnapshotRequest,
            AppJsonSerializerContext.Default.TerminalSnapshotResponse,
            cancellationToken);
    }

    private static async Task<TResponse> SendAsync<TRequest, TResponse>(
        HttpMethod method,
        string path,
        TRequest? payload,
        JsonTypeInfo<TRequest>? requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken)
        where TResponse : class
    {
        var settings = ReadSettings();
        using var http = new HttpClient { BaseAddress = new Uri(settings.ApiBaseUrl) };
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation("viberails_session", settings.SessionToken);
        request.Headers.TryAddWithoutValidation("viberails_tab", settings.TabToken);

        if (payload != null && requestTypeInfo != null)
        {
            var json = JsonSerializer.Serialize(payload, requestTypeInfo);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancellationToken);
            throw new InvalidOperationException($"VibeRails tool API call failed ({(int)response.StatusCode}): {error}");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync(stream, responseTypeInfo, cancellationToken);
        if (result == null)
        {
            throw new InvalidOperationException("VibeRails tool API returned an empty response.");
        }

        return result;
    }

    private static (string ApiBaseUrl, string SessionToken, string TabToken) ReadSettings()
    {
        var apiBaseUrl = Environment.GetEnvironmentVariable(LocalToolApiContext.ApiBaseUrlVariable);
        var sessionToken = Environment.GetEnvironmentVariable(LocalToolApiContext.SessionTokenVariable);
        var tabToken = Environment.GetEnvironmentVariable(LocalToolApiContext.TabTokenVariable);

        if (string.IsNullOrWhiteSpace(apiBaseUrl)
            || string.IsNullOrWhiteSpace(sessionToken)
            || string.IsNullOrWhiteSpace(tabToken))
        {
            throw new InvalidOperationException(
                "VibeRails terminal tools are unavailable because callback credentials were not found. Launch the agent from a VibeRails-managed terminal.");
        }

        return (apiBaseUrl.Trim().TrimEnd('/'), sessionToken.Trim(), tabToken.Trim());
    }

    private static string? ResolveTabId(string? requestedTabId)
    {
        if (!string.IsNullOrWhiteSpace(requestedTabId))
        {
            return requestedTabId.Trim();
        }

        var currentTabId = Environment.GetEnvironmentVariable(LocalToolApiContext.CurrentTabIdVariable);
        return string.IsNullOrWhiteSpace(currentTabId) ? null : currentTabId.Trim();
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
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
                // Fall through and return the raw body.
            }

            return content;
        }
        catch
        {
            return response.ReasonPhrase ?? "Unknown error";
        }
    }
}
