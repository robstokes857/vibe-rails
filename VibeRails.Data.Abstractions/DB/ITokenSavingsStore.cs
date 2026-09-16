using VibeRails.DTOs;
using VibeRails.Services;
using TokenSaver;
using TokenSaver.Pipeline;

namespace VibeRails.DB;


/// <summary>
/// Running totals over three windows: all time (all days/providers), the current UTC month, and
/// this app run ("session"). Bytes are measured; tokens are a display heuristic.
/// </summary>
public sealed record TokenSavingsTotals(
    long BytesBefore,
    long BytesAfter,
    long SessionBytesSaved = 0,
    long MonthBytesBefore = 0,
    long MonthBytesAfter = 0)
{
    public long BytesSaved => BytesBefore - BytesAfter;

    /// <summary>
    /// ~4 bytes/token heuristic, derived only at read time (never persisted) so history reprices
    /// for free if a real tokenizer ever replaces the estimate.
    /// </summary>
    public long TokensSaved => BytesSaved / 4;

    /// <summary>
    /// Savings the table has gained since this process started — a delta, not a before/after pair,
    /// because it spans every process writing to state.db and not just this one's own requests.
    /// </summary>
    public long SessionTokensSaved => SessionBytesSaved / 4;

    public long MonthTokensSaved => (MonthBytesBefore - MonthBytesAfter) / 4;
}

/// <summary>
/// Persists the token-saver tally to state.db (one row per UTC day per provider) and serves the
/// running totals the UI displays.
///
/// The table is shared by every VibeRails process, and the ones that matter are the terminal-tab
/// children: each tab hosts its own LLM proxy, so the CLI's savings are recorded there, never in
/// the root backend that serves the dashboard. That makes the persisted table — not any single
/// process's memory — the only place where "what has this app saved" exists.
/// </summary>
public interface ITokenSavingsStore
{
    /// <summary>
    /// Records one rewritten request. Never blocks and never throws: the in-memory total updates
    /// synchronously; the DB upsert runs fire-and-forget with failures swallowed (deliberate —
    /// savings telemetry is never worth a slower or riskier relay; worst case an increment is lost).
    /// </summary>
    void Record(string provider, int bytesBefore, int bytesAfter);

    /// <summary>
    /// Latest known totals: the most recent read of the persisted table, plus this process's own
    /// records that have not landed in it yet. Never waits on SQLite, so a caller that needs the
    /// other processes' newest rows awaits <see cref="RefreshAsync"/> first.
    /// </summary>
    TokenSavingsTotals GetTotals();

    /// <summary>
    /// Re-reads the persisted totals so this process sees what the other processes have saved.
    /// Await this before displaying a number; never call it from the relay hot path. Never throws:
    /// a failed refresh leaves the previous snapshot in place.
    /// </summary>
    Task RefreshAsync();
}

