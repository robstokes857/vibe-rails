using System.Net;

namespace VibeRails.Routes;

/// <summary>
/// Where the MCP Explorer connects, and which destination may receive the dashboard's own
/// credential pair. Only one may: the in-process <c>/mcp</c> on the exact loopback address and
/// port the Explorer's request arrived on. The <c>Host</c> header is not evidence of that port,
/// and another process can own a different 127.x address (or the other loopback family) on the
/// same port number, so neither decides anything here. VIBE-310.
/// </summary>
internal static class McpExplorerTarget
{
    private const string McpPath = "/mcp";

    /// <summary>The in-process MCP endpoint on the listener a request arrived on.</summary>
    public static Uri Own(IPAddress? localAddress, int localPort) =>
        new UriBuilder(Uri.UriSchemeHttp, OwnAddress(localAddress).ToString(), localPort, McpPath).Uri;

    /// <summary>
    /// Resolves the Explorer's endpoint field. Blank, a local path, or <c>localhost</c> on our own
    /// port and <c>/mcp</c> all mean this backend; anything else is used as written.
    /// </summary>
    public static Uri Resolve(string? endpoint, IPAddress? localAddress, int localPort)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return Own(localAddress, localPort);

        endpoint = endpoint.Trim();
        if (endpoint.StartsWith('/'))
            return new Uri(Own(localAddress, localPort), endpoint);

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Endpoint must be an absolute HTTP or HTTPS URL.");
        }

        var namesThisBackend = uri.Scheme == Uri.UriSchemeHttp
            && string.Equals(uri.DnsSafeHost, "localhost", StringComparison.OrdinalIgnoreCase)
            && uri.Port == localPort
            && IsMcpPath(uri);
        return namesThisBackend ? Own(localAddress, localPort) : uri;
    }

    /// <summary>True only for <see cref="Own"/>: the one destination that gets the dashboard credentials.</summary>
    public static bool IsOwn(Uri endpoint, IPAddress? localAddress, int localPort) =>
        endpoint.Scheme == Uri.UriSchemeHttp
        && endpoint.Port == localPort
        && IsMcpPath(endpoint)
        && IPAddress.TryParse(endpoint.DnsSafeHost, out var address)
        && Normalize(address).Equals(OwnAddress(localAddress));

    private static bool IsMcpPath(Uri uri) =>
        string.Equals(uri.AbsolutePath.TrimEnd('/'), McpPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>The backend listens on loopback only; anything else falls back to 127.0.0.1.</summary>
    private static IPAddress OwnAddress(IPAddress? localAddress)
    {
        var address = localAddress is null ? IPAddress.Loopback : Normalize(localAddress);
        return address.Equals(IPAddress.Loopback) || address.Equals(IPAddress.IPv6Loopback) ? address : IPAddress.Loopback;
    }

    private static IPAddress Normalize(IPAddress address) =>
        address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
}
