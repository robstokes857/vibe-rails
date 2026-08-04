using System.Diagnostics;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

public enum DataExportStage
{
    Idle,
    Snapshot,
    Compressing,
    Hashing,
    Uploading
}

/// <summary>
/// A point-in-time view of the running export, polled by the settings UI.
/// <paramref name="TotalBytes"/> is 0 when the current stage has no known total, which the client
/// renders as an indeterminate bar rather than inventing a percentage.
/// </summary>
public sealed record DataExportProgressSnapshot(
    bool Active,
    int RunId,
    string Stage,
    long ProcessedBytes,
    long TotalBytes,
    long ElapsedMs,
    long StageElapsedMs);

public interface IDataExportProgress
{
    void Begin();

    /// <summary>Starts a stage and resets the byte counter. Pass 0 when no total is known.</summary>
    void SetStage(DataExportStage stage, long totalBytes);

    /// <summary>Adds to the current stage's byte count. Safe to call from any thread.</summary>
    void Advance(long bytes);

    /// <summary>Sets the current stage's byte count to an absolute value.</summary>
    void SetProcessed(long processedBytes);

    void End();

    DataExportProgressSnapshot Current { get; }
}

/// <summary>
/// Progress for the one export that can be running in this process. The export holds a
/// process-wide gate and a cross-process lock, so a single slot is all that is ever needed.
///
/// The byte counter is the hot path — it is written once per stream chunk and once per uploaded
/// block — so it is a bare interlocked counter. Everything else lives in one immutable state
/// object that is only replaced at the four stage transitions.
/// </summary>
public sealed class DataExportProgress : IDataExportProgress
{
    private sealed record State(
        bool Active,
        int RunId,
        DataExportStage Stage,
        long TotalBytes,
        long RunStartedTimestamp,
        long StageStartedTimestamp);

    private static readonly State Idle = new(false, 0, DataExportStage.Idle, 0, 0, 0);

    private State _state = Idle;
    private long _processedBytes;
    private int _runId;

    public void Begin()
    {
        var runId = Interlocked.Increment(ref _runId);
        var now = Stopwatch.GetTimestamp();
        Interlocked.Exchange(ref _processedBytes, 0);
        Volatile.Write(
            ref _state,
            new State(true, runId, DataExportStage.Snapshot, 0, now, now));
    }

    public void SetStage(DataExportStage stage, long totalBytes)
    {
        var current = Volatile.Read(ref _state);
        if (!current.Active)
        {
            // A stray report from a torn-down run must not resurrect the progress display.
            return;
        }

        Interlocked.Exchange(ref _processedBytes, 0);
        Volatile.Write(
            ref _state,
            current with
            {
                Stage = stage,
                TotalBytes = Math.Max(0, totalBytes),
                StageStartedTimestamp = Stopwatch.GetTimestamp()
            });
    }

    public void Advance(long bytes)
    {
        if (bytes > 0)
            Interlocked.Add(ref _processedBytes, bytes);
    }

    public void SetProcessed(long processedBytes)
        => Interlocked.Exchange(ref _processedBytes, Math.Max(0, processedBytes));

    public void End()
    {
        var current = Volatile.Read(ref _state);
        Interlocked.Exchange(ref _processedBytes, 0);

        // Keep the run id so a poll that arrives just after the export finishes can still tell
        // which run it missed, but zero everything else — a finished run must never leave stale
        // numbers on screen.
        Volatile.Write(ref _state, Idle with { RunId = current.RunId });
    }

    public DataExportProgressSnapshot Current
    {
        get
        {
            var state = Volatile.Read(ref _state);
            if (!state.Active)
                return new DataExportProgressSnapshot(false, state.RunId, "idle", 0, 0, 0, 0);

            var processed = Interlocked.Read(ref _processedBytes);
            if (state.TotalBytes > 0)
            {
                // The counter and the state are read separately, so a stage that flips between the
                // two reads can briefly pair a new total with an old count. Clamping keeps that
                // from ever showing as more than 100%.
                processed = Math.Min(processed, state.TotalBytes);
            }

            return new DataExportProgressSnapshot(
                true,
                state.RunId,
                ToWireName(state.Stage),
                processed,
                state.TotalBytes,
                (long)Stopwatch.GetElapsedTime(state.RunStartedTimestamp).TotalMilliseconds,
                (long)Stopwatch.GetElapsedTime(state.StageStartedTimestamp).TotalMilliseconds);
        }
    }

    internal static string ToWireName(DataExportStage stage) => stage switch
    {
        DataExportStage.Snapshot => "snapshot",
        DataExportStage.Compressing => "compressing",
        DataExportStage.Hashing => "hashing",
        DataExportStage.Uploading => "uploading",
        _ => "idle"
    };
}

/// <summary>No-op sink so the service can be constructed without progress reporting.</summary>
public sealed class NullDataExportProgress : IDataExportProgress
{
    public static readonly NullDataExportProgress Instance = new();

    private NullDataExportProgress()
    {
    }

    public void Begin()
    {
    }

    public void SetStage(DataExportStage stage, long totalBytes)
    {
    }

    public void Advance(long bytes)
    {
    }

    public void SetProcessed(long processedBytes)
    {
    }

    public void End()
    {
    }

    public DataExportProgressSnapshot Current => new(false, 0, "idle", 0, 0, 0, 0);
}

/// <summary>
/// Read-only pass-through that reports every byte read. Deliberately does not dispose the stream
/// it wraps — the caller's <c>await using</c> still owns it.
/// </summary>
internal sealed class ProgressReadStream(Stream inner, IDataExportProgress progress) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        if (read > 0)
            progress.Advance(read);
        return read;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        var read = await inner.ReadAsync(buffer, cancellationToken);
        if (read > 0)
            progress.Advance(read);
        return read;
    }

    public override Task<int> ReadAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();
}
