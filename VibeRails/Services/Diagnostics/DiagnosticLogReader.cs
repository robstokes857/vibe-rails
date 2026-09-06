using System.Globalization;
using System.Text;
using VibeRails.DTOs;

namespace VibeRails.Services.Diagnostics;

/// <summary>Read-only, on-demand access to the application's existing Serilog diagnostics.</summary>
public interface IDiagnosticLogReader
{
    Task<InternalLogResponse> ReadAsync(string source, FeatureLogQuery query,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads bounded tails of application and daemon logs. Construction does no I/O, and concurrent
/// requests share a short-lived snapshot. Historical messages do not imply structured upload outcomes.
/// </summary>
public sealed class DiagnosticLogReader : IDiagnosticLogReader
{
    private readonly DiagnosticLogOptions _options;
    private readonly Cache _application = new();
    private readonly Cache _daemon = new();

    public DiagnosticLogReader() : this(new DiagnosticLogOptions()) { }

    public DiagnosticLogReader(DiagnosticLogOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DirectoryPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxFiles, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxBytesPerFile, 128);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxReadEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxMessageChars, 128);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxDirectoryEntries, 1);
        _options = options;
    }

    public async Task<InternalLogResponse> ReadAsync(string source, FeatureLogQuery query,
        CancellationToken cancellationToken = default)
    {
        var cache = source switch
        {
            "application" => _application,
            "daemon" => _daemon,
            _ => throw new ArgumentException("Diagnostic source must be application or daemon.", nameof(source))
        };
        await cache.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Snapshot snapshot;
        try
        {
            if (cache.Snapshot == null || Environment.TickCount64 - cache.CachedAt >= _options.ReadCacheDuration.TotalMilliseconds)
            {
                cache.Snapshot = await ReadSnapshotAsync(source, cancellationToken).ConfigureAwait(false);
                cache.CachedAt = Environment.TickCount64;
            }
            snapshot = cache.Snapshot;
        }
        finally { cache.Gate.Release(); }

        var feature = Filter(query.Feature, 128);
        var level = Filter(query.Level, 32);
        var status = Filter(query.Status, 64);
        var operation = Filter(query.OperationId, 128);
        var search = Filter(query.Search, 256);
        var limit = Math.Clamp(query.Limit, 1, 200);
        var page = snapshot.Entries.Where(e => Matches(e.Feature, feature) && Matches(e.Level, level) &&
                Matches(e.Status, status) && Matches(e.OperationId, operation) &&
                (string.IsNullOrEmpty(search) || Contains(e, search)))
            .Skip(Math.Clamp(query.Offset, 0, 10_000)).Take(limit + 1).ToArray();
        // Failures belong to the snapshot the page came from. Refreshing over the same damaged
        // line or locked file again must not make the count grow.
        return new InternalLogResponse(page.Take(limit).ToArray(), snapshot.Features, page.Length > limit,
            0, 0, snapshot.ReadFailures, Truncated: snapshot.Truncated);
    }

    private async Task<Snapshot> ReadSnapshotAsync(string source, CancellationToken cancellationToken)
    {
        var read = new ReadState(_options.MaxReadEntries);
        var files = FindFiles(source == "application" ? "vb-" : "vbd-", read, cancellationToken);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                // Check again at open time: a rotation may have replaced an enumerated path.
                if (IsReparsePoint(new DirectoryInfo(_options.DirectoryPath)) ||
                    IsReparsePoint(new FileInfo(file.FullName)))
                {
                    read.Failures++;
                    continue;
                }
                await ReadFileAsync(file, source, read, cancellationToken).ConfigureAwait(false);
            }
            // Rotation/deletion is normal while the app writes. The next refresh enumerates again.
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                read.Failures++;
            }
        }
        var entries = read.Newest.UnorderedItems.Select(item => item.Element)
            .OrderByDescending(e => e.TimestampUtc).ThenByDescending(e => e.Id, StringComparer.Ordinal).ToArray();
        return new Snapshot(entries, entries.Select(e => e.Feature).Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase).ToArray(), read.Failures, read.Truncated);
    }

    private FileInfo[] FindFiles(string prefix, ReadState read, CancellationToken cancellationToken)
    {
        var candidates = new List<(FileInfo File, DateTime LastWriteUtc, DateTime Date, int Sequence)>();
        try
        {
            var directory = new DirectoryInfo(_options.DirectoryPath);
            if (IsReparsePoint(directory))
            {
                read.Failures++;
                return [];
            }
            var scanned = 0;
            var enumeration = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false };
            foreach (var item in directory.EnumerateFileSystemInfos("*", enumeration))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++scanned > _options.MaxDirectoryEntries)
                {
                    read.Truncated = true;
                    break;
                }
                if (!TryFileName(item.Name, prefix, out var date, out var sequence))
                    continue;
                if (item is not FileInfo file || IsReparsePoint(file))
                {
                    read.Failures++;
                    continue;
                }
                // The enumerated timestamp can be stale for a file another process still has
                // open, which is exactly the file most likely to hold the newest lines.
                candidates.Add((file, LogFileWriteTime.ResolveUtc(file), date, sequence));
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (FileNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            read.Failures++;
        }
        if (candidates.Count > _options.MaxFiles)
            read.Truncated = true;
        // Concurrent processes can keep writing an earlier numbered file long after later
        // suffixes were created, so newest means most recently written, not highest suffix.
        return candidates.OrderByDescending(c => c.LastWriteUtc)
            .ThenByDescending(c => c.Date).ThenByDescending(c => c.Sequence)
            .Take(_options.MaxFiles).Select(c => c.File).ToArray();
    }

    private static bool TryFileName(string name, string prefix, out DateTime date, out int sequence)
    {
        date = default;
        sequence = 0;
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(".log", StringComparison.Ordinal))
            return false;
        var middle = name.AsSpan(prefix.Length, name.Length - prefix.Length - 4);
        if (middle.Length < 8 || !DateTime.TryParseExact(middle[..8], "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date))
            return false;
        if (middle.Length == 8)
            return true;
        // Serilog adds _001, _002, ... when a date's file is already held by another process.
        return middle.Length is >= 12 and <= 15 && middle[8] == '_' &&
            middle[9..].IndexOfAnyExceptInRange('0', '9') < 0 &&
            int.TryParse(middle[9..], NumberStyles.None, CultureInfo.InvariantCulture, out sequence);
    }

    private async Task ReadFileAsync(FileInfo file, string source, ReadState read, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file.FullName, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 16 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        var length = stream.Length;
        var count = (int)Math.Min(length, _options.MaxBytesPerFile);
        var offset = length - count;
        if (offset > 0)
        {
            read.Truncated = true;
            stream.Seek(offset, SeekOrigin.Begin);
        }
        var bytes = new byte[count];
        var readCount = 0;
        while (readCount < count)
        {
            var chunk = await stream.ReadAsync(bytes.AsMemory(readCount), cancellationToken).ConfigureAwait(false);
            if (chunk == 0)
                break;
            readCount += chunk;
        }
        ParseFile(bytes.AsSpan(0, readCount), offset, file.Name, source, read, cancellationToken);
    }

    private void ParseFile(ReadOnlySpan<byte> bytes, long fileOffset, string name, string source,
        ReadState read, CancellationToken cancellationToken)
    {
        var start = 0;
        var skipFirst = fileOffset > 0;
        var waitingForHeader = true;
        InternalLogEntry? current = null;
        var message = new StringBuilder();
        while (start < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var end = bytes[start..].IndexOf((byte)'\n');
            if (end < 0)
                break; // Never publish the active writer's incomplete last line.
            var lineOffset = fileOffset + start;
            var lineBytes = bytes.Slice(start, end);
            start += end + 1;
            if (skipFirst)
            {
                skipFirst = false;
                continue;
            }
            // Decoding is bounded by the file-tail limit; messages retained in the snapshot are smaller.
            var line = Encoding.UTF8.GetString(lineBytes).AsSpan().TrimEnd('\r');
            if (lineOffset == 0)
                line = line.TrimStart('\uFEFF');
            if (TryHeader(line, out var timestamp, out var level))
            {
                StoreCurrent();
                var body = line[30..];
                current = new InternalLogEntry($"{source}:{name}:{lineOffset:D16}", timestamp,
                    Feature(body), level, "diagnostic", "", Subject: name, Source: source, SourceFile: name);
                AppendMessage(body, message, read);
                waitingForHeader = false;
            }
            else if (LooksLikeHeader(line) || line.Contains('\uFFFD'))
            {
                StoreCurrent();
                read.Failures++;
                waitingForHeader = true;
            }
            else if (current != null)
            {
                AppendMessage("\n", message, read);
                AppendMessage(line, message, read);
            }
            else if (!waitingForHeader || fileOffset == 0)
            {
                if (!line.IsWhiteSpace())
                    read.Failures++;
            }
        }
        StoreCurrent();

        void StoreCurrent()
        {
            if (current == null)
                return;
            read.Add(current with { Message = message.ToString() });
            current = null;
            message.Clear();
        }
    }

    private void AppendMessage(ReadOnlySpan<char> text, StringBuilder message, ReadState read)
    {
        var remaining = _options.MaxMessageChars - message.Length;
        message.Append(text[..Math.Min(text.Length, remaining)]);
        if (text.Length > remaining)
            read.Truncated = true;
    }

    private static bool TryHeader(ReadOnlySpan<char> line, out DateTimeOffset timestamp, out string level)
    {
        timestamp = default;
        level = "";
        if (line.Length < 30 || line[23] != ' ' || line[24] != '[' || line[28] != ']' || line[29] != ' ' ||
            !DateTime.TryParseExact(line[..23], "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal, out var local))
            return false;
        level = line.Slice(25, 3) switch
        {
            "VRB" => "Trace", "DBG" => "Debug", "INF" => "Information", "WRN" => "Warning",
            "ERR" => "Error", "FTL" => "Critical", _ => ""
        };
        if (level.Length == 0)
            return false;
        timestamp = new DateTimeOffset(local).ToUniversalTime();
        return true;
    }

    private static bool LooksLikeHeader(ReadOnlySpan<char> line) => line.Length >= 25 &&
        char.IsAsciiDigit(line[0]) && line[4] == '-' && line[7] == '-' && line[24] == '[';

    private static string Feature(ReadOnlySpan<char> message)
    {
        if (message.Length < 3 || message[0] != '[')
            return "general";
        var end = message.IndexOf(']');
        if (end is < 2 or > 128)
            return "general";
        foreach (var character in message[1..end])
            if (!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.' or '/'))
                return "general";
        return message[1..end].ToString();
    }

    private static bool IsReparsePoint(FileSystemInfo item)
    {
        var attributes = item.Attributes;
        if (attributes == (FileAttributes)(-1))
            throw new FileNotFoundException("Diagnostic file or directory was removed.");
        return (attributes & FileAttributes.ReparsePoint) != 0;
    }
    private static string? Filter(string? value, int limit) => (value?.Length > limit ? value[..limit] : value)?.Trim();
    private static bool Matches(string? value, string? filter) => string.IsNullOrEmpty(filter) ||
        string.Equals(value, filter, StringComparison.OrdinalIgnoreCase);
    private static bool Contains(InternalLogEntry entry, string search) =>
        entry.Message.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        entry.Feature.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        entry.EventName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
        (entry.SourceFile?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false);

    private sealed class Cache
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public Snapshot? Snapshot { get; set; }
        public long CachedAt { get; set; }
    }

    private sealed record Snapshot(InternalLogEntry[] Entries, string[] Features, long ReadFailures, bool Truncated);

    private sealed class ReadState(int maxEntries)
    {
        public PriorityQueue<InternalLogEntry, (DateTimeOffset, string)> Newest { get; } = new();
        public long Failures { get; set; }
        public bool Truncated { get; set; }

        public void Add(InternalLogEntry entry)
        {
            Newest.Enqueue(entry, (entry.TimestampUtc, entry.Id));
            if (Newest.Count <= maxEntries)
                return;
            Newest.Dequeue();
            Truncated = true;
        }
    }
}
