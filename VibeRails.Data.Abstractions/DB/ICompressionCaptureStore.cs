using VibeRails.DTOs;
using VibeRails.Services;
using TokenSaver;
using TokenSaver.Pipeline;

namespace VibeRails.DB;


/// <summary>
/// One capture as the list view needs it. The absence of RawText/CompressedText is the point: the
/// table is uncapped and a single row can hold megabytes, so the big strings are only ever fetched
/// one capture at a time via <see cref="ICompressionCaptureStore.GetAsync"/>.
/// </summary>
public sealed record CompressionCaptureSummary(
    Guid Id,
    DateTime CreatedUtc,
    string Provider,
    string ToolName,
    string? Command,
    int CharsBefore,
    int CharsAfter,
    bool Changed,
    bool RewriteAccepted);

/// <summary>
/// One capture in full — the unit used to judge compression correctness, reconstituted from state.db.
///
/// <see cref="RawText"/> is byte-for-byte what the pipeline saw, which is what makes the what-if
/// preview real: feeding it back through a different plan is the same operation the proxy performed,
/// not a simulation of it. Persisting a truncated or normalized RawText would quietly turn the
/// preview into a lie, which is why the table stores it verbatim and uncapped.
/// </summary>
public sealed record CompressionCaptureDetail(
    Guid Id,
    DateTime CreatedUtc,
    string Provider,
    string ToolName,
    string? Command,
    string RawText,
    string CompressedText,
    int CharsBefore,
    int CharsAfter,
    bool Changed,
    bool RewriteAccepted,
    IReadOnlyList<StageTrace> Trace,
    IReadOnlyList<string> EnabledIds);

/// <summary>
/// Persists raw before/after compression captures to state.db and serves them back to the capture
/// view and the what-if preview.
/// </summary>
public interface ICompressionCaptureStore
{
    /// <summary>
    /// Records one tool_result's compression. Never blocks and never throws: the row is written
    /// fire-and-forget with failures swallowed. This is called from the LLM relay's hot path, where
    /// a dropped capture is a bad afternoon and a blocked relay is a broken product.
    /// </summary>
    void Record(CompressionCapture capture);

    /// <summary>
    /// Newest-first page of capture summaries. <paramref name="take"/> is clamped to
    /// <see cref="CompressionCaptureStore.MaxTake"/> — the table has no retention policy, so an
    /// unbounded take is a request to load the entire capture history into memory.
    /// </summary>
    Task<List<CompressionCaptureSummary>> ListAsync(int take, int skip, CancellationToken cancellationToken);

    /// <summary>One capture in full, or null when <paramref name="id"/> is unknown.</summary>
    Task<CompressionCaptureDetail?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every capture and returns the row count. Exists because the table is deliberately
    /// uncapped: with no retention policy, an explicit reset is the only eviction there is.
    /// </summary>
    Task<int> ClearAsync(CancellationToken cancellationToken);
}

