using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace PyBridge;

/// <summary>
/// A warm, long-lived Python worker process with a dead-simple request/response contract:
/// <b>one line of stdin per request, one line of stdout per reply</b>. Pay interpreter
/// startup and imports (e.g. <c>import torch</c>, model load) once, then make many cheap
/// calls — instead of a fresh process per call.
///
/// <code>
/// await using var session = runner.StartSession("worker.py");
/// await session.WaitForReadyAsync("ready");
/// var reply = await session.SendAsync("""{"op": "ping"}""");
/// var typed = await session.SendJsonAsync(request, MyContext.Default.Request, MyContext.Default.Reply);
/// </code>
///
/// The worker side is trivial Python (see <c>python/worker.py</c> for a reference):
/// read a line from stdin, print exactly one reply line, flush, repeat; exit on EOF.
/// </summary>
/// <remarks>
/// <para>Requests are serialized internally, so a session is safe to share between concurrent
/// callers — calls queue up in order. Anything the worker writes to stderr (logging,
/// warnings, tracebacks) is surfaced through <see cref="StandardErrorLine"/> and is also
/// in <see cref="Completion"/>'s captured output; it never corrupts replies.</para>
/// <para>One sharp edge, by design: cancelling a request <i>after it was written</i> abandons
/// a reply in flight, so request/reply alignment would be lost. The session detects this and
/// poisons itself (<see cref="IsFaulted"/>) — every later call throws instead of silently
/// returning the wrong reply. Dispose it and start a fresh session.</para>
/// </remarks>
public sealed class PythonSession : IAsyncDisposable
{
    private static readonly TimeSpan GracefulShutdownTimeout = TimeSpan.FromSeconds(5);

    private readonly PythonRun _run;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Exception? _fault;
    private int _disposeState;

    /// <summary>
    /// Wraps an interactive run in the one-line-in/one-line-out session protocol.
    /// Usually created via <c>runner.StartSession(...)</c>; use this directly when you need
    /// custom interpreter arguments: <c>new PythonSession(runner.StartInteractive([...]))</c>.
    /// </summary>
    /// <exception cref="ArgumentException">The run was not started with <c>StartInteractive</c>.</exception>
    public PythonSession(PythonRun run)
    {
        if (!run.SupportsLiveInput)
        {
            throw new ArgumentException(
                "A session needs live stdin. Start the run with StartInteractive(...) " +
                "(or use runner.StartSession(...)).", nameof(run));
        }

        _run = run;
    }

    /// <summary>The underlying live run, for advanced scenarios (e.g. <see cref="PythonRun.KillAsync"/>).</summary>
    public PythonRun Run => _run;

    /// <summary>Completes when the worker process exits, with its fully captured result.</summary>
    public Task<PythonResult> Completion => _run.Completion;

    /// <summary>
    /// True when a request was interrupted mid-protocol (cancelled or faulted after being
    /// written), which loses request/reply alignment. A faulted session refuses further
    /// requests — dispose it and start a new one.
    /// </summary>
    public bool IsFaulted => Volatile.Read(ref _fault) is not null;

    /// <summary>
    /// Raised for worker stderr lines (logs, progress, warnings) as they are encountered
    /// while a request or ready-wait is being read. Stderr written between requests is
    /// delivered when the next request reads past it; stderr after the last request may not
    /// be raised at all — it is always captured in <see cref="Completion"/>'s
    /// <see cref="PythonResult.StandardError"/> either way, and never interferes with
    /// request/reply matching. Handler exceptions are swallowed.
    /// </summary>
    public event Action<string>? StandardErrorLine;

    /// <summary>
    /// Waits for the worker to announce it is ready — the line your worker prints after its
    /// imports finish (e.g. <c>print("ready", flush=True)</c>). With a <paramref name="marker"/>,
    /// stdout lines are skipped until an exact match; otherwise the first non-empty stdout line
    /// counts. Returns the matched line.
    /// </summary>
    /// <exception cref="PythonExecutionException">The worker exited before becoming ready.</exception>
    public async Task<string> WaitForReadyAsync(string? marker = null, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfFaulted();
            return await ReadReplyLineAsync(marker, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Sends one request line to the worker and returns its one reply line.
    /// Blank stdout lines are skipped; stderr lines are routed to <see cref="StandardErrorLine"/>.
    /// </summary>
    /// <exception cref="PythonExecutionException">The worker exited instead of replying.</exception>
    public async Task<string> SendAsync(string requestLine, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            ThrowIfFaulted();

            try
            {
                await _run.WriteInputLineAsync(requestLine, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                throw await SessionDeadExceptionAsync("the request could not be delivered").ConfigureAwait(false);
            }

            try
            {
                return await ReadReplyLineAsync(marker: null, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not PythonExecutionException)
            {
                // The request was written but its reply was abandoned (e.g. the caller's token
                // fired mid-read). The next read would return THIS request's stale reply for a
                // future request — silently wrong data. Poison the session instead.
                Volatile.Write(ref _fault, ex);
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Typed request/response: serializes <paramref name="request"/> to a single JSON line,
    /// sends it, and deserializes the reply line. AOT/trim safe — you pass source-generated
    /// <see cref="JsonTypeInfo{T}"/> metadata, so no runtime reflection is involved.
    /// </summary>
    /// <exception cref="PythonExecutionException">The worker exited, or replied with null JSON.</exception>
    /// <exception cref="JsonException">The reply line was not valid JSON for <typeparamref name="TResponse"/>.</exception>
    public async Task<TResponse> SendJsonAsync<TRequest, TResponse>(
        TRequest request,
        JsonTypeInfo<TRequest> requestTypeInfo,
        JsonTypeInfo<TResponse> responseTypeInfo,
        CancellationToken cancellationToken = default)
    {
        var replyLine = await SendAsync(JsonSerializer.Serialize(request, requestTypeInfo), cancellationToken)
            .ConfigureAwait(false);

        return JsonSerializer.Deserialize(replyLine, responseTypeInfo)
            ?? throw new PythonExecutionException($"The worker's reply deserialized to null: {replyLine}");
    }

    /// <summary>
    /// Shuts the session down: sends EOF (a well-behaved worker exits on it), waits briefly,
    /// kills the process if it lingers, and never throws. Safe to call more than once.
    /// Await <see cref="Completion"/> first if you want to assert a clean exit.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
            return;

        _run.CompleteInput();

        try
        {
            await _run.Completion.WaitAsync(GracefulShutdownTimeout).ConfigureAwait(false);
        }
        catch
        {
            // Timed out or failed — PythonRun.DisposeAsync below kills and swallows.
        }

        await _run.DisposeAsync().ConfigureAwait(false);

        // Let any in-flight caller leave the critical section before the gate goes away —
        // the kill above unblocks their pending read, so this resolves promptly.
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeState) != 0, this);

    private void ThrowIfFaulted()
    {
        if (Volatile.Read(ref _fault) is { } fault)
        {
            throw new InvalidOperationException(
                "This session is unusable: a previous request was interrupted after being sent, " +
                "so request/reply alignment is lost. Dispose it and start a new session.", fault);
        }
    }

    private async Task<string> ReadReplyLineAsync(string? marker, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await _run.ReadOutputLineAsync(cancellationToken).ConfigureAwait(false);

            if (line is null)
                throw await SessionDeadExceptionAsync("no reply arrived").ConfigureAwait(false);

            if (line.Value.IsError)
            {
                try
                {
                    StandardErrorLine?.Invoke(line.Value.Text);
                }
                catch
                {
                    // A logging handler must not be able to desynchronize the protocol.
                }
                continue;
            }

            if (line.Value.Text.Length == 0)
                continue;

            if (marker is not null && !string.Equals(line.Value.Text, marker, StringComparison.Ordinal))
                continue;

            return line.Value.Text;
        }
    }

    private async Task<PythonExecutionException> SessionDeadExceptionAsync(string context)
    {
        PythonResult result;
        try
        {
            result = await _run.Completion.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new PythonExecutionException(
                $"The session's Python process failed and {context}. {ex.Message}", ex);
        }

        return new PythonExecutionException(
            $"The session's Python process exited with code {result.ExitCode} and {context}.\n" +
            $"Command: {result.CommandLine}\n--- stderr ---\n{result.StandardError}",
            result.ExitCode,
            result.StandardOutput,
            result.StandardError);
    }
}
