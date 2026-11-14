using System.Diagnostics;
using System.Text;
using CliWrap;

namespace VibeRails.Services.Git;

/// <summary>
/// Owns one-shot git processes. The interactive GitBlobBatchReader is the only exception.
/// CliWrap owns start, concurrent pipes, kill-tree and disposal; callers interpret the result.
/// </summary>
internal static class GitCommandRunner
{
    internal sealed record Result(
        int ExitCode, string StdOut, string StdErr, byte[] Bytes, bool TimedOut, bool OutputLimitExceeded);

    internal static Task<Result> RunTextAsync(
        string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        TimeSpan? timeout = null, int? maxOutputChars = null,
        IReadOnlyDictionary<string, string>? environment = null, ReadOnlyMemory<byte>? standardInput = null,
        bool killOnOutputLimit = false) =>
        RunAsync(() => GitCli.CreateStartInfo(directory, arguments, environment), cancellationToken,
            timeout, maxOutputChars, null, false, standardInput, killOnOutputLimit);

    internal static Task<Result> RunBinaryAsync(
        string directory, IReadOnlyList<string> arguments, CancellationToken cancellationToken,
        TimeSpan? timeout = null, int? maximumOutputBytes = null, ReadOnlyMemory<byte>? standardInput = null) =>
        RunAsync(() => GitCli.CreateStartInfo(directory, arguments), cancellationToken,
            timeout, null, maximumOutputBytes, true, standardInput, true);

    // A factory seam lets process regressions use a deterministic child without replacing PATH
    // or installing a fake git. Production callers always use the two git-only entry points above.
    internal static async Task<Result> RunAsync(
        Func<ProcessStartInfo> startInfoFactory, CancellationToken cancellationToken,
        TimeSpan? timeout = null, int? maxOutputChars = null, int? maximumOutputBytes = null,
        bool binaryOutput = false, ReadOnlyMemory<byte>? standardInput = null, bool killOnOutputLimit = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxOutputChars ?? 0);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumOutputBytes ?? 0);
        cancellationToken.ThrowIfCancellationRequested();

        using var deadline = new CancellationTokenSource(timeout ?? GitCli.DefaultTimeout);
        using var drainDeadline = new CancellationTokenSource();
        using var outputLimit = new CancellationTokenSource();
        using var forceful = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, deadline.Token, drainDeadline.Token, outputLimit.Token);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        using var bytes = new MemoryStream();
        var exceeded = false;
        var exitCode = -1;

        void LimitExceeded()
        {
            exceeded = true;
            if (killOnOutputLimit) outputLimit.Cancel();
        }

        try
        {
            var settings = startInfoFactory();
            var command = CliWrap.Cli.Wrap(settings.FileName)
                .WithValidation(CommandResultValidation.None)
                .WithStandardInputPipe(standardInput is { } input ? PipeSource.FromBytes(input) : PipeSource.Null)
                .WithStandardOutputPipe(PipeTarget.Create((stream, token) => binaryOutput
                    ? ReadBytesAsync(stream, bytes, maximumOutputBytes, LimitExceeded, token)
                    : ReadTextAsync(stream, stdout, maxOutputChars, LimitExceeded, token)))
                .WithStandardErrorPipe(PipeTarget.Create((stream, token) =>
                    // Prefix capture caps both streams; a hard catalog cap applies only to
                    // stdout, just like a binary cap. Diagnostics cannot overflow the catalog.
                    ReadTextAsync(stream, stderr, killOnOutputLimit ? null : maxOutputChars, LimitExceeded, token)));

            // CliWrap normally serializes argv into Arguments and sets the current directory.
            // Keep the exact ProcessStartInfo contract instead: argv only, -C, fsmonitor off,
            // absolute executable. In particular no repository path reaches WorkingDirectory.
            var result = await command.ExecuteAsync(
                configureProcessStartInfo: info =>
                {
                    info.WorkingDirectory = string.Empty;
                    info.Arguments = string.Empty;
                    foreach (var argument in settings.ArgumentList) info.ArgumentList.Add(argument);
                    foreach (var (name, value) in settings.Environment) info.Environment[name] = value;
                },
                configureProcess: process =>
                {
                    // 3.10.5 waits for ALL pipes after exit. A descendant may hold stdout open.
                    // Arm a separate bound on real process exit, even when the command timeout
                    // is minutes away. Enabling events also raises Exited for an already-dead child.
                    process.Exited += (_, _) =>
                    {
                        try { drainDeadline.CancelAfter(GitCli.DrainBound); }
                        catch (ObjectDisposedException) { /* all pipes already completed */ }
                    };
                    process.EnableRaisingEvents = true;
                },
                forcefulCancellationToken: forceful.Token).ConfigureAwait(false);
            exitCode = result.ExitCode;
        }
        catch (OperationCanceledException) when (forceful.IsCancellationRequested)
        {
            // Caller cancellation remains exceptional; our deadlines and output cap are results.
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(-1, stdout.ToString(), ex.GetBaseException().Message, bytes.ToArray(),
                deadline.IsCancellationRequested || drainDeadline.IsCancellationRequested, exceeded);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(exitCode, stdout.ToString(), stderr.ToString(), bytes.ToArray(),
            deadline.IsCancellationRequested || drainDeadline.IsCancellationRequested, exceeded);
    }

    private static async Task ReadTextAsync(Stream stream, StringBuilder output, int? limit,
        Action onLimit, CancellationToken token)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096, leaveOpen: true);
        var buffer = new char[4096];
        int read;
        while ((read = await ReadAsync(reader.ReadAsync(buffer.AsMemory(), token).AsTask(), token)) > 0)
        {
            var keep = limit is { } maximum ? Math.Min(read, maximum - output.Length) : read;
            output.Append(buffer, 0, keep);
            if (keep < read) onLimit();
        }
    }

    private static async Task ReadBytesAsync(Stream stream, MemoryStream output, int? limit,
        Action onLimit, CancellationToken token)
    {
        var buffer = new byte[81_920];
        int read;
        while ((read = await ReadAsync(stream.ReadAsync(buffer.AsMemory(), token).AsTask(), token)) > 0)
        {
            var keep = limit is { } maximum ? (int)Math.Min(read, maximum - output.Length) : read;
            output.Write(buffer, 0, keep);
            if (keep < read) onLimit();
        }
    }

    private static async Task<int> ReadAsync(Task<int> read, CancellationToken token)
    {
        // Anonymous-pipe reads need not honor cancellation once pending. Stop the target anyway;
        // CliWrap then disposes the stream. Observe a late fault without retaining output buffers.
        try { return await read.WaitAsync(token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            _ = read.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw;
        }
    }
}
