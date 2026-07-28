using System.Buffers;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
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

    // Stripped so the upstream answers in plain text. The relay tees response bytes straight into
    // the exchange log, and a gzip/Brotli body would land there as bytes that merely look like
    // text: UTF-8 decoding turns them into mojibake, and the capture is then useless for exactly
    // the compression analysis it exists to feed. The cost is bandwidth on the upstream hop only —
    // the client-facing leg is localhost, and the CLI never sees the difference.
    private const string AcceptEncodingHeader = "Accept-Encoding";

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
    /// <paramref name="provider"/> is the stable exchange-log key; it must not depend on whether a
    /// body transform happens to be enabled for this request.
    /// <paramref name="bodyTransform"/> is the provider-specific token-saver hook. A route passes
    /// null when saving is disabled; a request the transform declines or fails to rewrite is
    /// forwarded untouched. <paramref name="exchanges"/> is required because every authenticated
    /// request that reaches the upstream relay is recorded.
    /// </summary>
    internal static async Task HandleAsync(
        HttpContext context,
        IHttpClientFactory httpClientFactory,
        ILlmProxyAuthGate authGate,
        ILlmProxyEventSink events,
        Uri target,
        string provider,
        string activitySource,
        ILlmProxyBodyTransform? bodyTransform,
        CancellationToken cancellationToken,
        ILlmProxyExchangeSink exchanges)
    {
        if (!IsAuthenticated(context, authGate))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync("Unauthorized", cancellationToken);
            return;
        }

        var recorder = new ExchangeRecorder(provider, context.Request);

        TransformedRequestBody? transformed = null;
        try
        {
            if (bodyTransform is not null)
                transformed = await bodyTransform.TryTransformAsync(context.Request, events, cancellationToken);

            // Capture the request here and nowhere else. HttpRequestMessage takes ownership of the
            // content below and disposes it when this block exits, so reading the body back from a
            // failure path — or from the finally — is a use-after-dispose. That threw
            // ObjectDisposedException out of a finally block, which discarded the transport
            // handling below, dropped the row, and leaked the pooled lease. This is the one moment
            // the bytes are guaranteed valid.
            var upstreamBody = await CaptureRequestAsync(
                context.Request, transformed, recorder, cancellationToken);

            using var upstreamRequest = CreateUpstreamRequest(context, target, upstreamBody);
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
            recorder.StatusCode = (int)upstreamResponse.StatusCode;
            await TeeAsync(upstreamResponse, context.Response.Body, recorder, cancellationToken);
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
            // The recorder already owns private copies, so nothing here depends on the lease. Two
            // rules still hold: the sink must never replace an in-flight transport exception with
            // one of its own, and the pooled lease must be returned even when it does. A relay that
            // ended early still produces a row — a request whose response died halfway is exactly
            // the kind the log exists to explain.
            try
            {
                exchanges.Record(recorder.Build());
            }
            catch (Exception ex)
            {
                events.Diagnostic(activitySource, "exchange log record failed.", ex);
            }
            finally
            {
                // The forwarded HttpContent reads straight out of the transform's pooled buffers,
                // so the lease outlives the upstream send and is only returned here.
                transformed?.BufferLease?.Dispose();
            }
        }
    }

    /// <summary>
    /// Streams the upstream response to the client and copies it into <paramref name="recorder"/> on
    /// the way past. The client write happens FIRST on every chunk, so capture can never delay a
    /// token reaching the TUI; a capture that outgrows its cap stops copying and lets the stream
    /// continue untouched.
    /// </summary>
    private static async Task TeeAsync(
        HttpResponseMessage upstreamResponse,
        Stream client,
        ExchangeRecorder recorder,
        CancellationToken cancellationToken)
    {
        await using var upstream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await upstream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    return;

                await client.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                recorder.Response(buffer.AsSpan(0, read));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Accumulates one exchange. Deliberately dumb: it holds bytes and hands them over. Every
    /// decision about retention, redaction and storage belongs to the host's
    /// <see cref="ILlmProxyExchangeSink"/>, not to the relay.
    /// </summary>
    private sealed class ExchangeRecorder(string provider, HttpRequest request)
    {
        /// <summary>
        /// Cap on the retained copy of a response. The client always gets the whole thing; this
        /// only bounds how much of it one request can pin in memory on the relay's hot path, which
        /// an uncapped buffer would turn into an OOM vector under concurrency.
        /// </summary>
        internal const int MaxCapturedResponseBytes = 32 * 1024 * 1024;

        private readonly long _startedTicks = Stopwatch.GetTimestamp();
        private readonly string _method = request.Method;
        // Path only, never the query string — same rule the activity ping follows.
        private readonly string _path = request.Path.Value ?? "/";
        private readonly MemoryStream _response = new();
        private byte[]? _requestBefore;
        private byte[]? _requestAfter;
        private bool _responseTruncated;

        internal int StatusCode { get; set; }

        /// <summary>
        /// Takes ownership of the exact bytes sent upstream. Both arrays are private copies made
        /// while the source was still alive, so the recorder outlives the pooled lease and the
        /// forwarded <see cref="HttpContent"/> without depending on either.
        /// </summary>
        internal void Request(byte[] before, byte[] after)
        {
            _requestBefore = before;
            _requestAfter = after;
        }

        internal void Response(ReadOnlySpan<byte> chunk)
        {
            if (_responseTruncated)
                return;

            var room = MaxCapturedResponseBytes - (int)_response.Length;
            if (chunk.Length > room)
            {
                _response.Write(chunk[..Math.Max(room, 0)]);
                _responseTruncated = true;
                return;
            }

            _response.Write(chunk);
        }

        internal LlmProxyExchange Build()
        {
            var responseBytes = _response.GetBuffer();
            var responseLength = (int)_response.Length;
            return new LlmProxyExchange(
                Guid.NewGuid(),
                provider,
                _method,
                _path,
                StatusCode,
                Encoding.UTF8.GetString(_requestBefore ?? []),
                Encoding.UTF8.GetString(_requestAfter ?? []),
                Encoding.UTF8.GetString(
                    responseBytes, 0, CompleteUtf8Length(responseBytes, responseLength)),
                _responseTruncated,
                (int)Stopwatch.GetElapsedTime(_startedTicks).TotalMilliseconds);
        }

        /// <summary>
        /// Drops an incomplete UTF-8 sequence from the end of a truncated capture. The byte cap
        /// falls wherever the chunk boundary puts it, and a body cut mid-codepoint would otherwise
        /// decode with a trailing replacement character that every later analysis has to know to
        /// ignore. Untruncated captures always end on a boundary and are returned unchanged.
        /// </summary>
        private static int CompleteUtf8Length(byte[] bytes, int length)
        {
            // Walk back over continuation bytes (10xxxxxx), at most the three a 4-byte sequence can
            // have, to find the lead byte of whatever the cut landed inside.
            var start = length - 1;
            while (start >= 0 && start > length - 4 && (bytes[start] & 0xC0) == 0x80)
                start--;

            if (start < 0)
                return length;

            var sequence = bytes[start] switch
            {
                < 0x80 => 1,
                >= 0xF0 => 4,
                >= 0xE0 => 3,
                >= 0xC0 => 2,
                // A stray continuation byte is not a lead byte, so this is not a clean cut we can
                // reason about. Leave the buffer alone rather than guess.
                _ => 0
            };

            return sequence == 0 || start + sequence <= length ? length : start;
        }
    }

    private static bool IsAuthenticated(HttpContext context, ILlmProxyAuthGate authGate)
    {
        var sessionToken = context.Request.Headers[LlmProxyCodexConfig.SessionHeaderName].FirstOrDefault();
        var tabToken = context.Request.Headers[LlmProxyCodexConfig.TabHeaderName].FirstOrDefault();
        return authGate.ValidateSessionToken(sessionToken) && authGate.ValidateTabToken(tabToken);
    }

    /// <summary>
    /// Buffers whatever this request is actually sending upstream, hands the bytes to
    /// <paramref name="recorder"/>, and returns the content to forward.
    ///
    /// The passthrough branch buffers on purpose. Streaming the body straight through relays it
    /// correctly but records nothing, and an exchange log that silently omits every request the
    /// saver declined is worse than no log at all: the gap reads as "no traffic" rather than "not
    /// buffered", and those are precisely the requests a future compression stage needs to study.
    /// The transform path already buffers, so this only levels the two paths up.
    /// </summary>
    private static async Task<HttpContent?> CaptureRequestAsync(
        HttpRequest request,
        TransformedRequestBody? transformed,
        ExchangeRecorder recorder,
        CancellationToken cancellationToken)
    {
        if (transformed is not null)
        {
            var before = transformed.OriginalBody.ToArray();
            // ReadOnlyMemoryContent means the transform held the whole body, so the bytes that went
            // upstream can be read back exactly rather than re-derived by running the pipeline
            // again — a rerun could disagree with what was actually sent. A StreamContent instead
            // means the body outgrew the transform's cap and was never held whole; re-buffering it
            // here would defeat the cap that rejected it, so that request logs with empty bodies.
            var after = transformed.Content is ReadOnlyMemoryContent
                ? ReadBuffered(transformed.Content)
                : before;
            recorder.Request(before, after);
            return transformed.Content;
        }

        if (!RequestCanHaveBody(request))
        {
            recorder.Request([], []);
            return null;
        }

        var body = await ReadToArrayAsync(request.Body, cancellationToken);
        // No transform ran, so before and after are the same bytes by definition. Recording both
        // keeps every row the same shape and makes "saved nothing here" explicit in the data rather
        // than something a query has to infer from an absent row.
        recorder.Request(body, body);
        return new ByteArrayContent(body);
    }

    /// <summary>Copies an already-buffered content out while it is still alive.</summary>
    private static byte[] ReadBuffered(HttpContent content)
    {
        var stream = new MemoryStream();
        content.CopyTo(stream, null, CancellationToken.None);
        return stream.ToArray();
    }

    private static async Task<byte[]> ReadToArrayAsync(Stream body, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        await body.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static HttpRequestMessage CreateUpstreamRequest(
        HttpContext context, Uri target, HttpContent? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);

        // Already buffered by CaptureRequestAsync, whether the saver rewrote it or not.
        // Content-Length is computed from the actual bytes, and Content-Type still flows in via the
        // header copy below.
        request.Content = body;

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
        HopByHopHeaders.Contains(headerName)
        || LocalOnlyHeaders.Contains(headerName)
        || headerName.Equals(AcceptEncodingHeader, StringComparison.OrdinalIgnoreCase);
}
