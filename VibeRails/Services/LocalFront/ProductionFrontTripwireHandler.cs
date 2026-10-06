using Serilog;

namespace VibeRails.Services.LocalFront;

/// <summary>
/// Last line of defence for local Front mode: added to every IHttpClientFactory client of a
/// process that sees <see cref="LocalFrontMode.OriginVariable"/>, it fails any request to
/// viberails.ai or one of its subdomains before it leaves the machine. The feature-level pauses are
/// the real behavior; this catches a client someone adds later with a pinned production URL.
/// AI-provider and other hosts are untouched. It sees only the first hop: automatic redirects
/// happen inside the primary handler, so every client that carries a credential must also use
/// <c>MapRegisterServices.CreateNoRedirectHttpMessageHandler</c>.
/// </summary>
public sealed class ProductionFrontTripwireHandler : DelegatingHandler
{
    /// <summary>The production Front host.</summary>
    public const string ProductionHost = "viberails.ai";

    /// <summary>True for viberails.ai and its subdomains.</summary>
    public static bool IsProductionHost(Uri? uri)
    {
        var host = uri?.IdnHost.TrimEnd('.');
        return host is not null
            && (string.Equals(host, ProductionHost, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + ProductionHost, StringComparison.OrdinalIgnoreCase));
    }

    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (IsProductionHost(request.RequestUri))
        {
            // Method and path only: never the query, headers or body.
            Log.Warning("[LocalFront] Blocked {Method} {Host}{Path}: this process runs in local Front mode",
                request.Method, request.RequestUri!.IdnHost, request.RequestUri.AbsolutePath);
            throw new HttpRequestException(
                $"Local Front mode blocked a request to {request.RequestUri.IdnHost}. This VibeRails process talks only to the local Front.");
        }
        return base.SendAsync(request, cancellationToken);
    }
}
