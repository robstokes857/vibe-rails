namespace TokenSaver;

/// <summary>
/// Where the proxy sends the whole of what it relayed: the request body as the CLI sent it, the
/// request body as it went upstream, and the response that came back.
///
/// This exists because per-stage diagnostics answer the wrong question. A stage trace tells you
/// what the pipeline did on the day it ran, and the pipeline changes every time the compression
/// algorithm is touched — so yesterday's traces cannot be re-judged against today's stages. Raw
/// exchanges can: they are the input to every future what-if, including ones for stages that do
/// not exist yet. This is the durable artifact; traces are not.
///
/// It is also the only place the response body is retained at all. The relay streams responses
/// through untouched and never materializes them, so a sink is the sole way to see what a request
/// actually bought.
///
/// Consequences the host implementation owns, not this interface:
///   • An exchange is the most sensitive artifact this codebase produces. The request body carries
///     the system prompt, every user message, and the full contents of every file the agent has
///     read. Treat it as strictly more sensitive than SessionLogs: never checked in, never shipped
///     off-box. Exchange logging is always active for
///     authenticated traffic handled by a proxy route; there is deliberately no setting that can
///     disable it.
///   • Volume is unbounded and superlinear. The CLI resends the entire conversation every turn, so
///     consecutive exchanges in one session are near-duplicates of each other, each one larger than
///     the last. Storage must assume this.
///   • Implementations MUST return immediately and MUST NOT throw. This is called on the LLM hot
///     path, where a lost record is a bad afternoon and a blocked relay is a broken product.
/// </summary>
public interface ILlmProxyExchangeSink
{
    /// <summary>Records one relayed request/response pair. Fire-and-forget; never throws.</summary>
    void Record(LlmProxyExchange exchange);
}

/// <summary>
/// One relayed request and its response.
///
/// <paramref name="RequestBefore"/> and <paramref name="RequestAfter"/> are the same body either
/// side of the rewrite, so the difference between them is exactly what the saver did — measured on
/// the whole body, not summed from per-tool_result fragments. They are equal when the request was a
/// passthrough, which is itself the signal worth keeping: it says the pipeline saw this traffic and
/// declined it.
///
/// <paramref name="ResponseTruncated"/> marks a response that exceeded the capture cap. The client
/// still received it in full — only the retained copy is short — so a truncated record is evidence
/// about a large response, never evidence of a broken one.
/// </summary>
public sealed record LlmProxyExchange(
    Guid Id,
    string Provider,
    string Method,
    string Path,
    int StatusCode,
    string RequestBefore,
    string RequestAfter,
    string Response,
    bool ResponseTruncated,
    int ElapsedMs)
{
    public int RequestBytesBefore => RequestBefore.Length;

    public int RequestBytesAfter => RequestAfter.Length;

    /// <summary>Whole-body delta. Negative is possible and is not a bug: JSON re-serialization can
    /// grow a body whose text shrank (see the wire-size check in the rewriters).</summary>
    public int RequestBytesSaved => RequestBefore.Length - RequestAfter.Length;
}
