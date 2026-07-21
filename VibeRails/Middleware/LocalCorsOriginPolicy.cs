namespace VibeRails.Middleware;

internal static class LocalCorsOriginPolicy
{
    internal static bool IsAllowed(string? origin, int port)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        if (Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && uri.Scheme.Equals("vscode-webview", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return origin.Equals($"http://localhost:{port}", StringComparison.OrdinalIgnoreCase)
            || origin.Equals($"http://127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
            || origin.Equals($"http://[::1]:{port}", StringComparison.OrdinalIgnoreCase);
    }
}
