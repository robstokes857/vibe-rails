using TokenSaver;
using VibeRails.DB;

namespace VibeRails.Services.LlmProxy;

/// <summary>
/// Bridges the TokenSaver library's exchange seam to its own SQLite file. Sibling of
/// <see cref="CompressionCaptureSinkAdapter"/>, same division of labour: the library decides what an
/// exchange is and requires every proxied exchange to be recorded; the host decides where it lands.
/// </summary>
public sealed class LlmProxyExchangeSinkAdapter(ILlmExchangeLogStore store) : ILlmProxyExchangeSink
{
    public void Record(LlmProxyExchange exchange)
    {
        // Record is itself fire-and-forget and swallows its own failures, which is exactly what the
        // sink contract's "return immediately, never throw" requires.
        store.Record(exchange);
    }
}
