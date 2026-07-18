using System.Threading.Channels;

namespace PyBridge;

/// <summary>
/// A handle to a live, running Python process: read its output as it happens, optionally
/// write lines to its stdin while it runs, and await the final <see cref="PythonResult"/>.
///
/// <para>Obtained from <see cref="IPythonRunner.Start"/> (stdin fixed up-front, like a normal
/// run) or <see cref="IPythonRunner.StartInteractive"/> (stdin stays open so you can keep
/// sending lines — a two-way conversation with the process).</para>
///
/// <code>
/// await using var run = runner.StartInteractive(["-u", "worker.py"]);
/// await run.WriteInputLineAsync("hello");
/// await foreach (var line in run.Lines) { ... }
/// var result = await run.Completion;   // exit code, full stdout/stderr, timing
/// </code>
/// </summary>
/// <remarks>
/// <see cref="Lines"/> is a single-consumer stream: enumerate it from one place (or use
/// <see cref="ReadOutputLineAsync"/>, but not both concurrently). Everything the process
/// writes is also buffered, so <see cref="Completion"/> always has the full stdout/stderr
/// regardless of whether anyone consumed <see cref="Lines"/>.
/// </remarks>
public sealed class PythonRun : IAsyncDisposable
{
    private readonly ChannelReader<PythonOutputLine> _outputReader;
    private readonly ChannelWriter<string>? _inputWriter;
    private readonly CancellationTokenSource _killCts;
    private int _disposeState;

    internal PythonRun(
        ChannelReader<PythonOutputLine> outputReader,
        ChannelWriter<string>? inputWriter,
        Task<PythonResult> completion,
        CancellationTokenSource killCts)
    {
        _outputReader = outputReader;
        _inputWriter = inputWriter;
        Completion = completion;
        _killCts = killCts;
    }

    /// <summary>
    /// Completes when the process exits, with the same fully-captured <see cref="PythonResult"/>
    /// a buffered <c>RunAsync</c> would return (exit code, full stdout/stderr, timing).
    /// </summary>
    public Task<PythonResult> Completion { get; }

    /// <summary>
    /// True when the run was started with <c>StartInteractive</c> (a live stdin channel exists).
    /// This reflects how the run was started, not current liveness — after
    /// <see cref="CompleteInput"/> or process exit it stays true while
    /// <see cref="WriteInputLineAsync"/> throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public bool SupportsLiveInput => _inputWriter is not null;

    /// <summary>
    /// The process's stdout and stderr lines, merged in arrival order, live as the process runs.
    /// The stream ends when the process exits. Enumerate from a single consumer.
    /// </summary>
    public IAsyncEnumerable<PythonOutputLine> Lines => _outputReader.ReadAllAsync();

    /// <summary>
    /// Reads the next available output line, or <c>null</c> when the process has exited and all
    /// output has been consumed. A pull-style alternative to enumerating <see cref="Lines"/>.
    /// </summary>
    public async ValueTask<PythonOutputLine?> ReadOutputLineAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await _outputReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Sends one line to the process's stdin (a newline is appended and the pipe is flushed,
    /// so the process sees it immediately). Only valid for interactive runs.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The run was not started with <c>StartInteractive</c>, or the process has already exited.
    /// </exception>
    public async ValueTask WriteInputLineAsync(string line, CancellationToken cancellationToken = default)
    {
        if (_inputWriter is null)
        {
            throw new InvalidOperationException(
                "This run does not accept live input. Start it with StartInteractive(...) to stream stdin.");
        }

        try
        {
            await _inputWriter.WriteAsync(line, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            throw new InvalidOperationException(
                "Cannot write to stdin: the Python process has already exited (or input was completed). " +
                "Await Completion for the exit code and captured output.");
        }
    }

    /// <summary>
    /// Pumps every line of an async sequence into the process's stdin, then (by default)
    /// signals end-of-input. Lets a consumer stream input from any source:
    /// <code>await run.PipeInputAsync(ReadSensorLinesAsync());</code>
    /// </summary>
    /// <param name="lines">The lines to send, as they become available.</param>
    /// <param name="completeInput">Close stdin (EOF) after the sequence ends. Default: true.</param>
    /// <param name="cancellationToken">Stops pumping; the process itself keeps running.</param>
    public async Task PipeInputAsync(
        IAsyncEnumerable<string> lines,
        bool completeInput = true,
        CancellationToken cancellationToken = default)
    {
        await foreach (var line in lines.WithCancellation(cancellationToken).ConfigureAwait(false))
            await WriteInputLineAsync(line, cancellationToken).ConfigureAwait(false);

        if (completeInput)
            CompleteInput();
    }

    /// <summary>
    /// Signals end-of-input: stdin is closed once buffered lines are flushed, and the process
    /// sees EOF (the usual way to tell a line-reading script "we're done"). Safe to call more
    /// than once; a no-op for non-interactive runs.
    /// </summary>
    public void CompleteInput() => _inputWriter?.TryComplete();

    /// <summary>
    /// Kills the process immediately and returns the final result. If the kill was what ended
    /// the process, the result has <see cref="PythonResult.WasKilled"/> set (with whatever
    /// output was captured before death); if the process had already exited, the result
    /// reflects that real exit. If the run's <see cref="CancellationToken"/> had already been
    /// cancelled, the returned task faults with <see cref="OperationCanceledException"/>.
    /// Safe to call at any time, including after disposal.
    /// </summary>
    public Task<PythonResult> KillAsync()
    {
        try
        {
            _killCts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed — the process is gone; Completion has the outcome.
        }

        return Completion;
    }

    /// <summary>
    /// Completes input, kills the process if it is still running, and awaits teardown.
    /// Never throws (inspect <see cref="Completion"/> yourself if you care about the outcome)
    /// and is safe to call more than once.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        CompleteInput();

        var firstDispose = Interlocked.Exchange(ref _disposeState, 1) == 0;

        if (firstDispose && !Completion.IsCompleted)
            _killCts.Cancel();

        try
        {
            await Completion.ConfigureAwait(false);
        }
        catch
        {
            // Dispose must not throw; failures are observable via Completion.
        }

        if (firstDispose)
            _killCts.Dispose();
    }
}
