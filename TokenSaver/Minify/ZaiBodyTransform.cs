using System.Buffers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace TokenSaver.Minify;

/// <summary>
/// Buffers qualifying OpenAI Chat Completions POST bodies (the wire format OpenCode uses for
/// both the zai/Z.AI and xai/Grok providers) and applies <see cref="ChatCompletionsRewriter"/>.
/// As with the Anthropic and Codex transforms, every rewrite failure fails open to the original
/// buffered request — the proxy must never be able to break a request.
/// </summary>
internal sealed class ZaiBodyTransform(
    Pipeline.CompressionPlan plan,
    ICompressionCaptureSink? captures = null,
    string provider = "zai") : ILlmProxyBodyTransform
{
    internal const int MaxBufferedBodyBytes = 10 * 1024 * 1024;

    public string Provider => provider;

    public async ValueTask<TransformedRequestBody?> TryTransformAsync(
        HttpRequest request, ILlmProxyEventSink events, CancellationToken cancellationToken)
    {
        if (!AppliesTo(request) || request.ContentLength is > MaxBufferedBodyBytes)
            return null;

        var lease = new BufferLease((int)Math.Min(
            (request.ContentLength ?? 64 * 1024) + 1,
            MaxBufferedBodyBytes + 1));
        try
        {
            var (length, overflowed) = await BufferBodyAsync(request.Body, lease, cancellationToken);
            if (overflowed)
            {
                return new TransformedRequestBody(
                    new StreamContent(new PrefixedBodyStream(lease.Buffered(length), request.Body)),
                    Savings: null,
                    lease);
            }

            ToolOutputRewriteResult result;
            try
            {
                lease.Output = new PooledBufferWriter(length);
                result = ChatCompletionsRewriter.Rewrite(
                    lease.Buffered(length).Span,
                    plan,
                    lease.Output,
                    captures,
                    provider);
            }
            catch (Exception ex)
            {
                events.Diagnostic(
                    "OpenCode proxy", "token-saver rewrite failed; forwarding original body.", ex);
                result = new ToolOutputRewriteResult(false, length, length, 0, 0, default);
            }

            var original = lease.Buffered(length);
            var content = result.Rewritten
                ? new ReadOnlyMemoryContent(lease.Output!.WrittenMemory)
                : new ReadOnlyMemoryContent(original);
            return new TransformedRequestBody(content, result, lease, original);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    // OpenCode's zai provider uses the OpenAI Chat Completions endpoint. With the baseURL override
    // pointing at /llm/zai/api/paas/v4, requests land at /llm/zai/api/paas/v4/chat/completions — so
    // matching the /chat/completions suffix is provider-agnostic and survives a baseURL path change.
    private static bool AppliesTo(HttpRequest request) =>
        HttpMethods.IsPost(request.Method)
        && request.Path.Value?.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) == true
        && request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true
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

            if (length > MaxBufferedBodyBytes)
                return (length, true);
        }
    }

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
