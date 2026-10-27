using System.Net.Http;
using System.Text;
using TokenSaver;
using VibeRails.DTOs;

namespace VibeRails.Services.LlmProxy;

/// <summary>
/// The Settings "Test" button for an Enterprise Network Proxy. Sends one credential-free POST of
/// <c>{}</c> to the provider's real endpoint under the gateway, through the relay's own
/// <c>llm-proxy</c> client, so it crosses the same system/corporate proxy, TLS trust and no-redirect
/// transport that CLI traffic will. No CLI credential is read or sent and no response body is read
/// or returned: the answer is the HTTP status class, which says whether the URL is right.
/// </summary>
public sealed class EnterpriseNetworkProxyProbe(IHttpClientFactory clients)
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    // The endpoint each CLI posts to under its API base, and whether that base may be given
    // without /v1 (Claude Code appends it to ANTHROPIC_BASE_URL itself).
    private sealed record Endpoint(string Path, bool AddMissingApiPath);

    private static readonly Dictionary<string, Endpoint> Endpoints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["codex"] = new("/responses", false),
        ["claude"] = new("/messages", true),
        ["opencode"] = new("/chat/completions", false),
        ["grok"] = new("/responses", false)
    };

    private const string ApiPath = "/v1";

    /// <summary>True for a provider name the Settings page sends.</summary>
    public static bool IsKnownProvider(string? provider) => provider is not null && Endpoints.ContainsKey(provider);

    /// <summary>Tests <paramref name="url"/> as <paramref name="provider"/>'s gateway.</summary>
    public async Task<EnterpriseNetworkProxyTestResponse> TestAsync(string provider, string? url, CancellationToken ct)
    {
        var endpoint = Endpoints[provider];
        if (!EnterpriseNetworkProxy.TryNormalize(url, out var normalized))
            return new(false, "invalid", EnterpriseNetworkProxy.InvalidMessage, null, null, null);
        if (normalized.Length == 0)
            return new(false, "blank", "Enter a gateway URL to test. Blank uses the provider's own endpoint.", null, null, null);

        var apiBase = endpoint.AddMissingApiPath ? EnterpriseNetworkProxy.WithApiPath(normalized, ApiPath) : normalized;
        var result = await ProbeAsync(apiBase + endpoint.Path, ct);
        // OpenAI-style bases are used exactly as entered, as their CLIs use OPENAI_BASE_URL. The
        // usual mistake is leaving off /v1, so when nothing answers, see whether that would.
        if (result.Status != "not-found" || endpoint.AddMissingApiPath
            || normalized.EndsWith(ApiPath, StringComparison.OrdinalIgnoreCase))
            return result;
        var alternate = normalized + ApiPath;
        var retry = await ProbeAsync(alternate + endpoint.Path, ct);
        return retry.Ok
            ? result with { Message = $"{result.Message} It does answer at {alternate}.", SuggestedUrl = alternate }
            : result;
    }

    private async Task<EnterpriseNetworkProxyTestResponse> ProbeAsync(string target, CancellationToken ct)
    {
        var uri = new Uri(target);
        var host = uri.Authority;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
        try
        {
            using var response = await clients.CreateClient("llm-proxy")
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var code = (int)response.StatusCode;
            return code switch
            {
                401 or 403 => new(true, "reachable",
                    $"Reached {host}. It asked for credentials (HTTP {code}), as expected for a test that sends none; real requests carry the CLI's own key.",
                    target, code, null),
                (>= 200 and < 300) or 400 or 422 or 429 => new(true, "reachable",
                    $"Reached {host}, and the endpoint answered (HTTP {code}).", target, code, null),
                404 or 405 => new(false, "not-found",
                    $"Reached {host}, but nothing answered at {uri.AbsolutePath} (HTTP {code}). Check the API path.", target, code, null),
                407 => new(false, "proxy-auth",
                    "Your network proxy requires sign-in (HTTP 407) before anything reaches the gateway.", target, code, null),
                >= 300 and < 400 => new(false, "redirect",
                    $"{host} redirected (HTTP {code}). VibeRails never follows redirects, so enter the final URL.", target, code, null),
                _ => new(false, "error", $"Reached {host}, but it returned HTTP {code}.", target, code, null)
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, "timeout",
                $"No answer from {host} within {Timeout.TotalSeconds:0} seconds. Check the address, VPN and network proxy.", target, null, null);
        }
        catch (HttpRequestException ex)
        {
            return new(false, "unreachable", Describe(ex, host), target, null, null);
        }
    }

    private static string Describe(HttpRequestException ex, string host) => ex.HttpRequestError switch
    {
        HttpRequestError.NameResolutionError => $"Could not find {host}. Check the address, VPN or network.",
        HttpRequestError.SecureConnectionError =>
            $"Could not make a trusted HTTPS connection to {host}. If your company inspects TLS, its root certificate must be trusted by this computer.",
        HttpRequestError.ProxyTunnelError => $"The network proxy refused the connection to {host}.",
        HttpRequestError.ConnectionError => $"Could not connect to {host}. Check the address, port, VPN or firewall.",
        _ => $"Could not reach {host}: {ex.Message}"
    };
}
