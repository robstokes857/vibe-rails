using TokenSaver;
using VibeRails.DB;

namespace VibeRails.Services.LlmProxy;

/// <summary>
/// Bridges the TokenSaver library's capture seam to state.db: the library decides WHAT a capture is,
/// this decides whether captures are wanted and where they land. Sibling of
/// <see cref="LlmProxyEventSinkAdapter"/>, and the same division of labour — the library stays free
/// of both settings.json and SQLite.
/// </summary>
public sealed class CompressionCaptureSinkAdapter(ICompressionCaptureStore store) : ICompressionCaptureSink
{
    public void Capture(CompressionCapture capture)
    {
        // Straight forward: Record is itself fire-and-forget and swallows its own failures, which
        // is what the sink contract's "return immediately, never throw" requires.
        store.Record(capture);
    }
}
