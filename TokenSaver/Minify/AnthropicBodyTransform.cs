using System.Buffers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace TokenSaver.Minify;

/// <summary>
/// The Anthropic relay's body transform: buffers a qualifying <c>/v1/messages</c> POST (up to a
/// hard cap), runs <see cref="AnthropicMessagesRewriter"/> over it, and hands the relay either the
/// rewritten bytes or the buffered original. Everything here fails open — the worst outcome of any
/// failure is a request that saves nothing.
/// </summary>
internal sealed class AnthropicBodyTransform(MinifyFlags flags) : ILlmProxyBodyTransform
{
    /// <summary>
    /// Bodies over this are forwarded without a rewrite attempt. Message histories with base64
    /// images ride these requests, so the cap is generous; the guard exists so a pathological body
    /// can't balloon proxy memory.
    /// </summary>
    internal const int MaxBufferedBodyBytes = 10 * 1024 * 1024;

    public string Provider => "anthropic";

    public async ValueTask<TransformedRequestBody?> TryTransformAsync(
        HttpRequest request, ILlmProxyEventSink events, CancellationToken cancellationToken)
    {
        if (!AppliesTo(request))
            return null;

        // Declared-oversize bodies are never buffered at all — the relay streams them untouched.
        if (request.ContentLength is > MaxBufferedBodyBytes)
            return null;

        var lease = new BufferLease((int)Math.Min(
            (request.ContentLength ?? 64 * 1024) + 1,
            MaxBufferedBodyBytes + 1));
        try
        {
            var (length, overflowed) = await BufferBodyAsync(request.Body, lease, cancellationToken);
            if (overflowed)
            {
                // A chunked body outgrew the cap mid-read. The prefix is already off the wire, so
                // stitch it back in front of the live remainder and forward without rewriting.
                return new TransformedRequestBody(
                    new StreamContent(new PrefixedBodyStream(lease.Buffered(length), request.Body)),
                    Savings: null,
                    lease);
            }

            AnthropicRewriteResult result;
            try
            {
                lease.Output = new PooledBufferWriter(length);
                result = AnthropicMessagesRewriter.Rewrite(
                    lease.Buffered(length).Span,
                    flags,
                    AnthropicMessagesRewriter.DefaultToolAllowlist,
                    lease.Output);
            }
            catch (Exception ex)
            {
                // The rewriter fails open on JsonException itself; this is the catch-all mandated
                // by token_saving_plan.md §6 — the proxy must never be able to break a request.
                events.Diagnostic(
                    "Claude proxy", "token-saver rewrite failed; forwarding original body.", ex);
                result = new AnthropicRewriteResult(false, length, length, 0, 0, default);
            }

            var content = result.Rewritten
                ? new ReadOnlyMemoryContent(lease.Output!.WrittenMemory)
                : new ReadOnlyMemoryContent(lease.Buffered(length));
            return new TransformedRequestBody(content, result, lease);
        }
        catch
        {
            // Only client-body I/O errors and cancellation reach here (the rewrite is guarded
            // above); the request is unrecoverable either way, so release the pooled buffers and
            // let the relay's disconnect handling classify the exception.
            lease.Dispose();
            throw;
        }
    }

    private static bool AppliesTo(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
        && request.Path.Value?.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase) == true
        && request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
        // Feature first: HTTP/2 bodies can arrive with neither Content-Length nor Transfer-Encoding.
        && (request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody
            ?? (request.ContentLength > 0 || request.Headers.ContainsKey("Transfer-Encoding")));

    private static async ValueTask<(int Length, bool Overflowed)> BufferBodyAsync(
        Stream body, BufferLease lease, CancellationToken cancellationToken)
    {
        var length = 0;
        while (true)
        {
            if (length == lease.Input.Length)
                lease.GrowInput();

            var read = await body.ReadAsync(lease.Input.AsMemory(length), cancellationToken);
            if (read == 0)
                return (length, false);
            length += read;

            // Rent() can hand back a larger array than asked for, so the cap check must be on the
            // byte count, never on buffer fullness.
            if (length > MaxBufferedBodyBytes)
                return (length, true);
        }
    }

    /// <summary>
    /// Owns the pooled input buffer (and, once the rewrite runs, the pooled output writer) for one
    /// request. The relay disposes it after the upstream send completes, because the forwarded
    /// <see cref="HttpContent"/> reads straight out of these buffers.
    /// </summary>
    internal sealed class BufferLease(int initialCapacity) : IDisposable
    {
        public byte[] Input { get; private set; } =
            ArrayPool<byte>.Shared.Rent(Math.Max(initialCapacity, 4096));

        public PooledBufferWriter? Output { get; set; }

        public ReadOnlyMemory<byte> Buffered(int length) => Input.AsMemory(0, length);

        public void GrowInput()
        {
            var grown = ArrayPool<byte>.Shared.Rent(
                (int)Math.Min((long)Input.Length * 2, MaxBufferedBodyBytes + 1));
            Input.CopyTo(grown.AsSpan());
            ArrayPool<byte>.Shared.Return(Input);
            Input = grown;
        }

        public void Dispose()
        {
            ArrayPool<byte>.Shared.Return(Input);
            Input = [];
            Output?.Dispose();
            Output = null;
        }
    }
}
