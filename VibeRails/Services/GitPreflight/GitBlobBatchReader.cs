using System.Diagnostics;
using System.Globalization;
using System.Text;
using Serilog;
using VibeRails.Services.Git;

namespace VibeRails.Services.GitPreflight;

/// <summary>
/// Reads blobs by object id through one <c>git cat-file --batch</c> process (VIBE-269).
///
/// A snapshot used to spawn <c>git cat-file blob</c> once per file, about 52 ms each on Windows,
/// so a committed-scope capture that also read every supported source at HEAD for impact ranking
/// took close to two minutes on this repository. One long-lived process answers the same reads
/// without paying process start-up per file.
///
/// Requests go one at a time: write an id, read its header, then exactly the size it announces.
/// Nothing is pipelined, so git never sits on a full stdout pipe while this side sits on git's
/// stdin, and only one blob is held at once, never larger than the caller's limit. Ids are checked
/// to be hex before they are written, so a request cannot carry a newline into the line protocol.
///
/// The process is started on the first read, through <see cref="GitCli.CreateStartInfo"/> like
/// every other git launch. Its stdin is the request channel and is closed on disposal, which is
/// what ends the process. A read that fails, times out or desynchronizes the stream stops the
/// process and returns null; the next read starts a fresh one, so one bad blob costs only that
/// blob, as a failed per-file spawn did. Cancelling the caller's token stops the process and
/// throws.
///
/// Not thread-safe: a capture reads its blobs sequentially.
/// </summary>
internal sealed class GitBlobBatchReader : IAsyncDisposable
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ExitGracePeriod = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A header is "&lt;id&gt; &lt;type&gt; &lt;size&gt;" or "&lt;id&gt; missing": a SHA-256 id
    /// plus the longest type and a 64-bit size is well under this. Anything longer is not git.
    /// </summary>
    private const int MaximumHeaderBytes = 256;

    private readonly string _repositoryPath;
    private readonly TimeSpan _requestTimeout;
    private readonly byte[] _buffer = new byte[81_920];
    private int _bufferStart;
    private int _bufferEnd;
    private Process? _process;
    private Task<string>? _standardError;
    private bool _disposed;

    public GitBlobBatchReader(string repositoryPath, TimeSpan? requestTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        _repositoryPath = repositoryPath;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
    }

    /// <summary>How many git processes this reader has started; one for a healthy capture.</summary>
    internal int ProcessStartCount { get; private set; }

    /// <summary>The running process's id, so a test can end it between reads.</summary>
    internal int? ProcessIdForTest => _process?.Id;

    /// <summary>
    /// The blob's exact bytes, or null when the id is not a readable blob (missing, another object
    /// type), is larger than <paramref name="maximumBytes"/>, or git failed or timed out. An
    /// oversized blob is skipped on the stream, not read into memory.
    /// </summary>
    public async Task<byte[]?> TryReadBlobAsync(
        string objectId,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsObjectId(objectId) || maximumBytes < 0)
            return null;

        var process = await EnsureStartedAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_requestTimeout);
        // An anonymous-pipe read or write need not observe cancellation once it has started, so the
        // deadline kills git instead and the closed pipe ends the pending operation.
        await using var killOnDeadline = deadline.Token.Register(static state => TryKill((Process)state!), process);
        try
        {
            var stdin = process.StandardInput.BaseStream;
            await stdin.WriteAsync(Encoding.ASCII.GetBytes(objectId + "\n"), deadline.Token);
            await stdin.FlushAsync(deadline.Token);

            var header = await ReadHeaderAsync(process, deadline.Token);
            var fields = header.Split(' ');
            if (fields.Length == 0 || !fields[0].Equals(objectId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"git cat-file answered \"{header}\" for {objectId}.");
            // "<id> missing" (or "ambiguous"): nothing follows the header, so the stream is in step.
            if (fields.Length == 2)
                return null;
            if (fields.Length != 3
                || !long.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
            {
                throw new InvalidDataException($"git cat-file answered \"{header}\" for {objectId}.");
            }

            if (fields[1] != "blob" || size > maximumBytes || size > Array.MaxLength)
            {
                // Keep the stream in step without holding the object: skip its bytes and the LF.
                await SkipAsync(process, size + 1, deadline.Token);
                return null;
            }

            var content = new byte[size];
            await ReadExactlyAsync(process, content, deadline.Token);
            var terminator = new byte[1];
            await ReadExactlyAsync(process, terminator, deadline.Token);
            if (terminator[0] != (byte)'\n')
                throw new InvalidDataException($"git cat-file did not end {objectId} with a line feed.");
            return content;
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException
                                       or ObjectDisposedException)
        {
            // The process and the stream position are no longer trustworthy.
            var timedOut = deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            await StopAsync(graceful: false);
            cancellationToken.ThrowIfCancellationRequested();
            Log.Warning(
                "[GitPreflight] git cat-file --batch could not read {ObjectId} in {Repository}: {Reason}. "
                + "The blob is left unanalyzed; later reads start a new process.",
                objectId,
                _repositoryPath,
                timedOut ? $"no answer within {_requestTimeout.TotalSeconds:0} s" : ex.Message);
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        await StopAsync(graceful: true);
    }

    private async Task<Process> EnsureStartedAsync()
    {
        if (_process is { HasExited: false } running)
            return running;
        // Git ended between requests (or a deadline fired just as the last read finished): start
        // over rather than failing the next read on a dead pipe.
        await StopAsync(graceful: false);

        var process = new Process
        {
            StartInfo = GitCli.CreateStartInfo(_repositoryPath, ["cat-file", "--batch"])
        };
        try
        {
            process.Start();
        }
        catch
        {
            process.Dispose();
            throw;
        }

        ProcessStartCount++;
        _process = process;
        _bufferStart = 0;
        _bufferEnd = 0;
        // Drained throughout: a warning-heavy git must not stall on a full stderr pipe.
        _standardError = process.StandardError.ReadToEndAsync();
        return process;
    }

    private async Task StopAsync(bool graceful)
    {
        var process = _process;
        var standardError = _standardError;
        _process = null;
        _standardError = null;
        _bufferStart = 0;
        _bufferEnd = 0;
        if (process is null)
            return;

        try
        {
            if (graceful)
            {
                // EOF on stdin is how cat-file --batch finishes.
                try { process.StandardInput.Close(); }
                catch (IOException) { /* git already closed its end */ }

                using var grace = new CancellationTokenSource(ExitGracePeriod);
                try
                {
                    await process.WaitForExitAsync(grace.Token);
                }
                catch (OperationCanceledException)
                {
                    TryKill(process);
                }
            }
            else
            {
                TryKill(process);
            }

            if (standardError is not null)
            {
                try { await standardError.WaitAsync(ExitGracePeriod); }
                catch (Exception ex) when (ex is IOException or TimeoutException or ObjectDisposedException
                                               or OperationCanceledException)
                {
                    // Only drained so git never blocks on it; nothing reads its text.
                }
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task<string> ReadHeaderAsync(Process process, CancellationToken cancellationToken)
    {
        while (true)
        {
            var newline = Array.IndexOf(_buffer, (byte)'\n', _bufferStart, _bufferEnd - _bufferStart);
            if (newline >= 0)
            {
                var header = Encoding.ASCII.GetString(_buffer, _bufferStart, newline - _bufferStart);
                _bufferStart = newline + 1;
                return header;
            }

            if (_bufferEnd - _bufferStart >= MaximumHeaderBytes)
                throw new InvalidDataException("git cat-file sent a header line longer than any valid one.");
            if (!await FillAsync(process, cancellationToken))
                throw new EndOfStreamException("git cat-file --batch exited before answering.");
        }
    }

    private async Task ReadExactlyAsync(Process process, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var buffered = Math.Min(destination.Length, _bufferEnd - _bufferStart);
        _buffer.AsMemory(_bufferStart, buffered).CopyTo(destination);
        _bufferStart += buffered;
        if (buffered < destination.Length)
        {
            // Large remainders go straight into the destination instead of through the buffer.
            await process.StandardOutput.BaseStream.ReadExactlyAsync(destination[buffered..], cancellationToken);
        }
    }

    private async Task SkipAsync(Process process, long count, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            if (_bufferStart == _bufferEnd && !await FillAsync(process, cancellationToken))
                throw new EndOfStreamException("git cat-file --batch exited inside an object.");
            var skipped = (int)Math.Min(count, _bufferEnd - _bufferStart);
            _bufferStart += skipped;
            count -= skipped;
        }
    }

    /// <summary>Reads more output after what is buffered; false at end of stream.</summary>
    private async Task<bool> FillAsync(Process process, CancellationToken cancellationToken)
    {
        if (_bufferStart > 0)
        {
            var unread = _bufferEnd - _bufferStart;
            Buffer.BlockCopy(_buffer, _bufferStart, _buffer, 0, unread);
            _bufferStart = 0;
            _bufferEnd = unread;
        }

        var read = await process.StandardOutput.BaseStream.ReadAsync(
            _buffer.AsMemory(_bufferEnd),
            cancellationToken);
        _bufferEnd += read;
        return read > 0;
    }

    private static bool IsObjectId(string? value) =>
        value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Preserve the original failure, timeout or cancellation.
        }
    }
}
