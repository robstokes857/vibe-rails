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
