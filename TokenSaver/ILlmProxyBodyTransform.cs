using Microsoft.AspNetCore.Http;
using TokenSaver.Minify;

namespace TokenSaver;

/// <summary>
/// Optional request-body rewrite hook for <see cref="LlmProxyRelay"/>. Provider routes pass their
/// structured JSON transform when the token saver is enabled.
/// </summary>
internal interface ILlmProxyBodyTransform
{
    /// <summary>Provider tag carried into savings reports (e.g. "anthropic").</summary>
    string Provider { get; }

    /// <summary>
    /// Buffers and rewrites a qualifying request body. Returns null when the request doesn't
    /// qualify (wrong method/path/content-type, or a declared body over the cap) — the relay then
    /// streams the body untouched, exactly as a passthrough. Never throws for transform reasons
    /// (any rewrite failure fails open to the buffered original bytes); only I/O errors reading
    /// the client body propagate.
    /// </summary>
    ValueTask<TransformedRequestBody?> TryTransformAsync(
        HttpRequest request, ILlmProxyEventSink events, CancellationToken cancellationToken);
}

/// <summary>
/// A buffered (possibly rewritten) request body. <paramref name="Savings"/> is null on the
/// cap-overflow path where the body is forwarded without a rewrite attempt.
/// <paramref name="BufferLease"/> owns the pooled buffers backing <paramref name="Content"/> and
/// must stay alive until the upstream send completes; the relay disposes it.
/// </summary>
internal sealed record TransformedRequestBody(
    HttpContent Content,
    ToolOutputRewriteResult? Savings,
    IDisposable? BufferLease);
