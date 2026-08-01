using System.Net;

namespace TokenSaver;

public static class LlmProxyBaseUrl
{
    /// <summary>
    /// Normalizes a local API base URL: blank/whitespace falls back to the sentinel
    /// <c>http://127.0.0.1:0</c>, then it is trimmed and stripped of trailing slashes. The host
    /// app's <c>LocalToolApiContext.NormalizeBaseUrl</c> delegates here so every base URL — tool
    /// API and LLM proxy alike — is normalized one way.
    /// </summary>
    public static string Normalize(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value)
            ? "http://127.0.0.1:0"
            : value.Trim();

        return trimmed.TrimEnd('/');
    }

    /// <summary>
    /// <see cref="Normalize"/> plus proof that the result names a process on this machine: an
    /// absolute http/https URL whose host is a loopback address.
    ///
    /// Required before attaching proxy credentials to a request, because the base URL arrives in an
    /// environment variable (<c>VIBERAILS_LLM_PROXY_BASE</c>) and the session/tab tokens are sent as
    /// headers to whatever it names. Everything that writes that variable writes a loopback address
    /// today; this is what makes that a property of the code rather than of the current callers, so
    /// a tampered or inherited environment redirects the call to nowhere instead of posting the
    /// tokens to a remote host.
    ///
    /// Deliberately separate from <see cref="Normalize"/>: that one is also used for display and for
    /// the tool API's own base URL, and must keep accepting anything it is given.
    /// </summary>
    public static bool TryNormalizeLoopback(string? value, out string normalized)
    {
        normalized = Normalize(value ?? string.Empty);

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        return IsLoopbackHost(uri);
    }

    // Uri.IsLoopback covers "localhost", 127.0.0.1 and ::1 but not the rest of 127.0.0.0/8, so
    // parse the literal too — IPAddress.IsLoopback is the authority on the whole range.
    private static bool IsLoopbackHost(Uri uri)
    {
        if (uri.IsLoopback)
            return true;

        return uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            && IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address)
            && IPAddress.IsLoopback(address);
    }
}
