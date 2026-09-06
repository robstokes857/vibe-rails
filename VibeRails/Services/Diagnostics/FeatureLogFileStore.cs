using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services.Diagnostics;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InternalLogEntry))]
internal sealed partial class FeatureLogJsonContext : JsonSerializerContext;

/// <summary>One on-demand read of the retained segments and the problems met while reading them.</summary>
internal sealed record FeatureLogSnapshot(InternalLogEntry[] Entries, long ReadFailures)
{
    internal static FeatureLogSnapshot Empty { get; } = new([], 0);
}

/// <summary>Only the background consumer writes; UI reads use separate shared file handles.</summary>
internal sealed class FeatureLogFileStore(FeatureLogOptions options, string instanceId)
{
    private const int MaxLineBytes = 32 * 1024;
    private static readonly byte[] NewLine = [(byte)'\n'];
    private FileStream? _stream;
    private string? _activePath;
    private string? _directoryPath;
    private int _segmentSequence;
    private long _segmentBytes;

    private string DirectoryPath => _directoryPath ??= options.ResolveDirectoryPath();

    /// <summary>
    /// How many entries of the most recent <see cref="AppendAsync"/> call reached a flushed or
    /// closed segment. When the call throws, the remainder was lost and is what callers count.
    /// </summary>
    internal int LastBatchPersisted { get; private set; }

    internal async Task AppendAsync(IReadOnlyList<InternalLogEntry> entries,
        CancellationToken cancellationToken = default)
    {
        LastBatchPersisted = 0;
        for (var index = 0; index < entries.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(entries[index], FeatureLogJsonContext.Default.InternalLogEntry);
            if (_stream != null && _segmentBytes + bytes.Length + 1 > options.MaxSegmentBytes)
            {
                // Closing flushes and seals everything written so far in this batch.
                await CloseAsync(cancellationToken).ConfigureAwait(false);
                LastBatchPersisted = index;
            }
            if (_stream == null)
                OpenSegment();
            await _stream!.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(NewLine, cancellationToken).ConfigureAwait(false);
            _segmentBytes += bytes.Length + 1;
        }
        if (_stream != null)
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        LastBatchPersisted = entries.Count;
    }

    private void OpenSegment()
    {
        PrivateFilePermissions.EnsureDirectory(DirectoryPath);
        // A process id plus a random instance id keeps concurrently open dashboards independent.
        var name = $"feature-{DateTime.UtcNow.Ticks:D19}-{Environment.ProcessId}-{instanceId}-{++_segmentSequence:D6}.active.jsonl";
        _activePath = Path.Combine(DirectoryPath, name);
        _stream = new FileStream(_activePath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.Read,
            BufferSize = 16 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        PrivateFilePermissions.EnsureFile(_activePath);
        _segmentBytes = 0;
        Prune();
    }

    internal async Task CloseAsync(CancellationToken cancellationToken = default)
    {
        var stream = _stream;
        var path = _activePath;
        _stream = null;
        _activePath = null;
        if (stream == null)
            return;
        // FileStream disposal has no cancellation parameter. Bound the wait so a stalled
        // final flush can finish independently after the application's shutdown deadline.
        await stream.DisposeAsync().AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (path != null)
            File.Move(path, path.Replace(".active.jsonl", ".jsonl", StringComparison.Ordinal));
        Prune();
    }

    internal async Task CloseAfterFailureAsync(CancellationToken cancellationToken = default)
    {
        // Retry only on a later batch, in a fresh segment. A partially written final line must
        // never be joined to the next event; the reader ignores incomplete final lines.
        try { await CloseAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private void Prune()
    {
        try
        {
            var files = FilesNewestFirst().ToArray();
            var retained = files.Length;
            foreach (var file in files.Reverse())
            {
                if (retained <= options.MaxRetainedFiles)
                    break;
                if (file.FullName == _activePath || IsLiveActiveSegment(file.Name))
                    continue;
                try
                {
                    File.Delete(file.FullName);
                    retained--;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool IsLiveActiveSegment(string name)
    {
        if (!name.EndsWith(".active.jsonl", StringComparison.Ordinal))
            return false;
        var parts = name.Split('-');
        if (parts.Length != 5 || !int.TryParse(parts[2], out var pid))
            return true; // Unknown ownership is never grounds for deleting an active file.
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }

    // Ranked by the handle-accurate write time: other dashboards' active segments would otherwise
    // be ordered by a stale directory entry (see LogFileWriteTime) and could fall outside the
    // retained window while still receiving events.
    private IEnumerable<FileInfo> FilesNewestFirst() => new DirectoryInfo(DirectoryPath)
        .EnumerateFiles("feature-*.jsonl", SearchOption.TopDirectoryOnly)
        .Select(file => (File: file, LastWriteUtc: LogFileWriteTime.ResolveUtc(file)))
        .OrderByDescending(item => item.LastWriteUtc)
        .ThenByDescending(item => item.File.Name, StringComparer.Ordinal)
        .Select(item => item.File);

    internal async Task<FeatureLogSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var read = new ReadState(options.MaxReadEntries);
        FileInfo[] files;
        try
        {
            files = FilesNewestFirst().Take(options.MaxRetainedFiles).ToArray();
        }
        catch (DirectoryNotFoundException) { return FeatureLogSnapshot.Empty; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FeatureLogSnapshot([], 1);
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await ReadFileAsync(file.FullName, read, cancellationToken).ConfigureAwait(false);
            }
            // A writer may rename or prune a closed segment during enumeration; just skip it.
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                read.Failures++;
            }
        }
        var entries = read.Newest.UnorderedItems.Select(item => item.Element)
            .OrderByDescending(e => e.TimestampUtc).ThenByDescending(e => e.Id, StringComparer.Ordinal).ToArray();
        return new FeatureLogSnapshot(entries, read.Failures);
    }

    private async Task ReadFileAsync(string path, ReadState read, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 16 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        // Even a damaged/hand-edited segment cannot make a UI read allocate or scan indefinitely.
        var length = (int)Math.Min(stream.Length, Math.Max(options.MaxSegmentBytes, MaxLineBytes));
        var skipsFirstLine = stream.Length > length;
        if (skipsFirstLine)
            stream.Seek(-length, SeekOrigin.End);
        var bytes = new byte[length];
        var count = 0;
        while (count < bytes.Length)
        {
            var chunk = await stream.ReadAsync(bytes.AsMemory(count), cancellationToken).ConfigureAwait(false);
            if (chunk == 0)
                break;
            count += chunk;
        }
        var start = 0;
        for (var i = 0; i < count; i++)
        {
            if (bytes[i] != (byte)'\n')
                continue;
            if (!skipsFirstLine)
                ReadLine(bytes.AsSpan(start, i - start), read);
            skipsFirstLine = false;
            start = i + 1;
        }
        // Ignore a final line without '\n': an active writer may not have flushed it yet.
    }

    private static void ReadLine(ReadOnlySpan<byte> line, ReadState read)
    {
        if (line.IsEmpty)
            return;
        if (line.Length > MaxLineBytes)
        {
            read.Failures++;
            return;
        }
        try
        {
            var entry = JsonSerializer.Deserialize(line, FeatureLogJsonContext.Default.InternalLogEntry);
            if (entry == null || !Valid(entry))
            {
                read.Failures++;
                return;
            }
            read.Add(entry);
        }
        catch (JsonException) { read.Failures++; }
    }

    private static bool Valid(InternalLogEntry entry) =>
        entry.Id is { Length: > 0 and <= 128 } && entry.TimestampUtc != default &&
        entry.Feature is { Length: > 0 and <= 128 } && entry.Level is { Length: > 0 and <= 32 } &&
        entry.EventName is { Length: <= 128 } && entry.Message is { Length: <= 2048 } &&
        (entry.OperationId == null || entry.OperationId.Length <= 128) &&
        (entry.Subject == null || entry.Subject.Length <= 512) &&
        (entry.Status == null || entry.Status.Length <= 64);

    private sealed class ReadState(int maxEntries)
    {
        public PriorityQueue<InternalLogEntry, (DateTimeOffset, string)> Newest { get; } = new();
        public long Failures { get; set; }

        public void Add(InternalLogEntry entry)
        {
            Newest.Enqueue(entry, (entry.TimestampUtc, entry.Id));
            if (Newest.Count > maxEntries)
                Newest.Dequeue();
        }
    }
}
