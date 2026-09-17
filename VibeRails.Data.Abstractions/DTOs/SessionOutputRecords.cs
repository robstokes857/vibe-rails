namespace VibeRails.DTOs;

public record SessionLogChunkRecord(
    long Id,
    DateTime TimestampUtc,
    byte[] Content
);

public record TerminalSessionLogRecord(
    long Id,
    string SessionId,
    int Sequence,
    bool IsAlternateScreen,
    byte[] Data,
    int Cols,
    int Rows,
    DateTime TimestampUtc
);

public enum TerminalOutputKind
{
    /// <summary>One raw PTY chunk, exactly as read; lands in SessionLogs.</summary>
    Legacy,
    /// <summary>A replay frame split at alt-screen / sync-output boundaries; lands in TerminalSessionLogs.</summary>
    Enriched,
}

/// <summary>
/// One row of a terminal-output batch. A drain of the session writer produces legacy and
/// enriched rows in arrival order and persists them in a single transaction, so the writer
/// lock is taken once per drain instead of twice per PTY read.
/// </summary>
public sealed record TerminalOutputWrite(
    TerminalOutputKind Kind,
    byte[] Data,
    bool IsError,
    int Sequence,
    bool IsAlternateScreen,
    int Cols,
    int Rows,
    DateTime TimestampUtc)
{
    public static TerminalOutputWrite Legacy(byte[] data, bool isError, DateTime timestampUtc)
        => new(TerminalOutputKind.Legacy, data, isError, 0, false, 0, 0, timestampUtc);

    public static TerminalOutputWrite Enriched(int sequence, byte[] data, bool isAlternateScreen, int cols, int rows, DateTime timestampUtc)
        => new(TerminalOutputKind.Enriched, data, false, sequence, isAlternateScreen, cols, rows, timestampUtc);
}
