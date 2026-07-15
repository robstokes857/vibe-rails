using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace TokenSaver;

/// <summary>
/// Shared plumbing for the local LLM reverse proxies — <see cref="LlmProxyRoutes"/> (OpenAI/Codex)
/// and <see cref="LlmAnthropicProxyRoutes"/> (Anthropic/Claude). Each route owns the parts that
/// differ per provider (enable flag, upstream URI, activity-source label); this type owns the parts
/// that are identical: the target URI shape, the header allow/deny lists, the forwarded upstream
/// request, the shared auth gate, and the streaming relay — including the SSE-friendly infinite
/// timeout and the client-disconnect handling.
/// </summary>
internal static class LlmProxyRelay
{
    private const string UpstreamScheme = "https";

    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection",
        "Keep-Alive",
        "Proxy-Authenticate",
        "Proxy-Authorization",
        "TE",
        "Trailer",
        "Transfer-Encoding",
        "Upgrade"
    };

    // Local-only headers stripped before forwarding upstream: per-hop specifics plus the proxy's own
    // auth handshake (cookie + session/tab tokens), so the upstream provider never receives
    // VibeRails' local auth secrets. The session/tab header names are shared across both providers.
    private static readonly HashSet<string> LocalOnlyHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Content-Length",
        "Cookie",
        LlmProxyCodexConfig.SessionHeaderName,
        LlmProxyCodexConfig.TabHeaderName
    };

    /// <summary>
    /// Builds the upstream target: <paramref name="pathPrefix"/> is stripped from the incoming path,
    /// the remainder is forwarded to <paramref name="host"/> over HTTPS, and the query string is
    /// carried through verbatim. Host and scheme are fixed by the caller, so only the path/query are
    /// request-controlled (no host or protocol confusion).
    /// </summary>
    internal static Uri BuildTarget(HttpRequest request, string host, string pathPrefix)
    {
        var path = request.Path.ToUriComponent();
        var rest = path.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase)
            ? path.AsSpan(pathPrefix.Length)
            : ReadOnlySpan<char>.Empty;

        var builder = new UriBuilder
        {
            Scheme = UpstreamScheme,
            Host = host,
            Path = rest.IsEmpty ? "/" : string.Concat("/", rest)
        };

        var query = request.QueryString.Value;
        if (!string.IsNullOrEmpty(query))
            builder.Query = query[0] == '?' ? query[1..] : query;

        return builder.Uri;
    }

    /// <summary>
    /// Authenticates the proxy request (session + tab tokens) then relays it to <paramref name="target"/>.
    /// The caller has already confirmed the feature is enabled and built the provider-specific target.
    /// <paramref name="bodyTransform"/> is the provider-specific token-saver hook. A route passes
    /// null when saving is disabled; a request the transform declines or fails to rewrite is
    /// forwarded untouched.
    /// </summary>
    internal static async Task HandleAsync(
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        ILlmProxyAuthGate authGate,
        ILlmProxyEventSink events,
        Uri target,
        string activitySource,
        ILlmProxyBodyTransform? bodyTransform,
        CancellationToken cancellationToken)
    {
        if (!IsAuthenticated(context, authGate))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized", cancellationToken);
            return;
        }

        TransformedRequestBody? transformed = null;
        try
        {
            if (bodyTransform is not null)
                transformed = await bodyTransform.TryTransformAsync(context.Request, events, cancellationToken);

            using var upstreamRequest = CreateUpstreamRequest(context, target, transformed?.Content);
            var http = httpClientFactory.CreateClient();
            // An SSE turn streams for as long as the model keeps generating, which routinely outlasts
            // HttpClient's 100 s default timeout — that firing would abort the read mid-turn. Leave it
            // unbounded and rely on the client's RequestAborted token to end the relay when the CLI quits.
            http.Timeout = Timeout.InfiniteTimeSpan;

            using var upstreamResponse = await http.SendAsync(
                upstreamRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            // Only delivered requests are measured — a rejected one (4xx/5xx) never reaches the
            // model, so tallying it would overstate what we saved. Unrewritten-but-measured
            // requests report too (zero savings): the seen-vs-rewritten gap is the canary for an
            // upstream tool rename silently zeroing the feature.
            var savings = transformed?.Savings;
            var measured = savings is not null
                && (int)upstreamResponse.StatusCode is >= 200 and < 300;
            if (measured)
            {
                events.SavingsMeasured(new LlmProxySavingsReport(
                    bodyTransform!.Provider,
                    savings!.BytesBefore,
                    savings.BytesAfter,
                    savings.ToolResultsMinified,
                    savings.ToolResultsSeen,
                    savings.Transforms,
                    savings.Condensed));
            }

            // Display-only activity ping that lights the proxy indicator: never carries the query
            // string, request body, or local auth headers — only the method, upstream path, status,
            // and (when minified) the byte savings.
            events.ProxyActivity(
                source: activitySource,
                label: context.Request.Method,
                target: target.GetLeftPart(UriPartial.Path),
                status: ((int)upstreamResponse.StatusCode).ToString(),
                bytesSaved: measured && savings!.Rewritten ? savings.BytesSaved : null);

            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            CopyResponseHeaders(upstreamResponse, context.Response);

            // The model response is SSE — disable response buffering so the TUI renders tokens live,
            // then stream it back chunk-by-chunk, untouched.
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await upstreamResponse.Content.CopyToAsync(context.Response.Body, cancellationToken);
        }
        catch (Exception ex) when (ex is OperationCanceledException
            or IOException
            or HttpRequestException { InnerException: IOException })
        {
            // Transport teardown. Cooperative cancellation surfaces as OperationCanceledException;
            // an aborted socket read/write surfaces as an IOException that HttpContent re-wraps as
            // HttpRequestException — and that shape covers BOTH sides of the relay: the CLI closing
            // its end (turn finished, Ctrl-C'd) AND the upstream dying post-connect (RST while
            // uploading the body, awaiting headers, or mid-SSE — HttpIOException derives from
            // IOException). Which side failed decides the outcome; a swallowed upstream failure
            // must never fabricate an empty HTTP 200 "success" for the CLI.
            events.Diagnostic(
                activitySource,
                "relay ended early (client disconnect or transport teardown).",
                ex);

            if (context.RequestAborted.IsCancellationRequested)
            {
                // Genuine client disconnect: nobody is listening, nothing to send.
            }
            else if (!context.Response.HasStarted)
            {
                // Upstream transport failure before anything was relayed: surface a retryable
                // gateway error instead of letting Kestrel finalize a default 200 empty response.
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
            }
            else
            {
                // Upstream died mid-stream with the CLI still connected: abort the connection so
                // the CLI sees a transport error (retryable), not a cleanly-terminated response
                // that is missing its final SSE events.
                context.Abort();
            }
        }
        finally
        {
            // The forwarded HttpContent reads straight out of the transform's pooled buffers, so
            // the lease outlives the upstream send and is only returned here.
            transformed?.BufferLease?.Dispose();
        }
    }

    private static bool IsAuthenticated(HttpContext context, ILlmProxyAuthGate authGate)
    {
        var sessionToken = context.Request.Headers[LlmProxyCodexConfig.SessionHeaderName].FirstOrDefault();
        var tabToken = context.Request.Headers[LlmProxyCodexConfig.TabHeaderName].FirstOrDefault();
        return authGate.ValidateSessionToken(sessionToken) && authGate.ValidateTabToken(tabToken);
    }

    private static HttpRequestMessage CreateUpstreamRequest(
        HttpContext context, Uri target, HttpContent? transformedBody)
    {
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);

        if (transformedBody is not null)
        {
            // The token saver buffered (and possibly rewrote) the body; Content-Length is computed
            // from the actual bytes, and the Content-Type still flows in via the header copy below.
            request.Content = transformedBody;
        }
        else if (RequestCanHaveBody(context.Request))
        {
            // Passthrough: stream the request body upstream untouched.
            request.Content = new StreamContent(context.Request.Body);
        }

        foreach (var header in context.Request.Headers)
        {
            if (ShouldSkipRequestHeader(header.Key))
                continue;

            // Content-specific headers (e.g. Content-Type) are rejected by the request-header
            // collection, so they fall through onto the content headers instead.
            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                request.Content?.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        return request;
    }

    private static void CopyResponseHeaders(HttpResponseMessage upstreamResponse, HttpResponse response)
    {
        CopyHeaders(upstreamResponse.Headers, response.Headers);
        CopyHeaders(upstreamResponse.Content.Headers, response.Headers);
        // Kestrel owns the client-facing framing; a forwarded chunked marker would collide with it.
        response.Headers.Remove("transfer-encoding");
    }

    private static void CopyHeaders(HttpHeaders source, IHeaderDictionary destination)
    {
        foreach (var header in source)
        {
            if (HopByHopHeaders.Contains(header.Key))
                continue;

            destination[header.Key] = header.Value.ToArray();
        }
    }

    // Kestrel knows definitively whether a body exists — including HTTP/2 requests, which can
    // carry one with neither Content-Length nor Transfer-Encoding. The header heuristic is only
    // the fallback for hosts (e.g. bare test contexts) that don't populate the feature.
    private static bool RequestCanHaveBody(HttpRequest request) =>
        request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody
        ?? (request.ContentLength.GetValueOrDefault() > 0
            || request.Headers.ContainsKey("Transfer-Encoding"));

    private static bool ShouldSkipRequestHeader(string headerName) =>
        HopByHopHeaders.Contains(headerName) || LocalOnlyHeaders.Contains(headerName);
}
