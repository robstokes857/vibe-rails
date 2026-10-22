namespace VibeRails.DTOs;

/// <summary>The interactive recording representations supported by Chat History.</summary>
public enum SessionRecordingKind { Raw, Replay, TerminalReplay }

/// <summary>One raw recording chunk, including the error marker retained in raw archives.</summary>
public sealed record SessionRecordingChunk(long Id, DateTime TimestampUtc, byte[] Content, bool IsError);

/// <summary>A bounded, consistent snapshot of the rows needed for an interactive recording response.</summary>
public sealed record SessionRecordingData(
    List<SessionRecordingChunk> Chunks,
    List<UserInputRecord> Inputs,
    List<TerminalSessionLogRecord> TerminalLogs)
{
    /// <summary>Total source bytes allowed across all included tables.</summary>
    public const int MaximumBytes = 16 * 1024 * 1024;
    /// <summary>Total rows allowed, including empty chunks.</summary>
    public const int MaximumRows = 20_000;
}

/// <summary>The complete recording is retained, but is too large for an interactive response.</summary>
public sealed class SessionRecordingTooLargeException() : Exception("Recording exceeds the interactive limit of 16 MiB or 20,000 rows.");
