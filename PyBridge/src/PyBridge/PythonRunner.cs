using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using CliWrap;

namespace PyBridge;

/// <summary>
/// The default <see cref="IPythonRunner"/>: runs Python scripts, modules, or one-off code
/// as child processes and hands you back everything they produce — buffered
/// (<see cref="RunAsync"/>), streamed live (<see cref="StreamAsync"/>), or as a live
/// two-way handle (<see cref="Start"/> / <see cref="StartInteractive"/>).
///
/// The guarantee: if a command would run when you type it at a terminal, it runs here too,
/// and its input/output flow through to the caller. Built on
/// <see href="https://github.com/Tyrrrz/CliWrap">CliWrap</see>, which handles the genuinely
/// hard parts (async piping, no deadlocks, argument escaping, process teardown).
/// </summary>
/// <remarks>
/// Instances are cheap and hold no per-run state — create one per interpreter/environment and
/// reuse it. Every method is safe to call concurrently because each call spins up its own
/// process and buffers; just don't mutate <see cref="Options"/> while runs are in flight.
/// Convenience overloads (<c>RunFileAsync</c>, <c>StreamCodeAsync</c>, sessions, …) come from
/// <see cref="PythonRunnerExtensions"/> and work on any <see cref="IPythonRunner"/>.
/// </remarks>
public sealed class PythonRunner : IPythonRunner
{
    private static readonly SearchValues<char> DisplayQuoteTriggers = SearchValues.Create(" \t\"");
    private static readonly byte[] NewlineBytes = "\n"u8.ToArray();

    private readonly PythonRunnerOptions _options;

    /// <summary>Creates a runner using the given options (defaults to whatever <c>python</c> is on PATH).</summary>
    public PythonRunner(PythonRunnerOptions? options = null)
        => _options = options ?? new PythonRunnerOptions();

    /// <inheritdoc />
    public PythonRunnerOptions Options => _options;

    /// <summary>
    /// The zero-ceremony one-liner: run a script with the interpreter discovered on this
    /// machine (venv → conda → PATH) and get the captured result back.
    /// </summary>
    public static Task<PythonResult> QuickRunAsync(string scriptPath, params ReadOnlySpan<string> arguments)
    {
        var args = new string[arguments.Length + 1];
        args[0] = scriptPath;
        arguments.CopyTo(args.AsSpan(1));
        return new PythonRunner(PythonRunnerOptions.Discover()).RunAsync(args);
    }

    /// <inheritdoc />
    public async Task<PythonResult> RunAsync(
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        Action<string>? onStandardOutputLine = null,
        Action<string>? onStandardErrorLine = null,
        CancellationToken cancellationToken = default)
    {
        var stdOutBuffer = new StringBuilder();
        var stdErrBuffer = new StringBuilder();

        // Always buffer into a StringBuilder so the full text ends up in the result.
        // If the caller also wants live lines, merge a delegate target alongside it.
        var stdOutPipe = onStandardOutputLine is null
            ? PipeTarget.ToStringBuilder(stdOutBuffer)
            : PipeTarget.Merge(PipeTarget.ToStringBuilder(stdOutBuffer), PipeTarget.ToDelegate(onStandardOutputLine));

        var stdErrPipe = onStandardErrorLine is null
            ? PipeTarget.ToStringBuilder(stdErrBuffer)
            : PipeTarget.Merge(PipeTarget.ToStringBuilder(stdErrBuffer), PipeTarget.ToDelegate(onStandardErrorLine));

        // Explicit UTF-8: CliWrap's default is the console input encoding, which would clash
        // with UseUtf8Io telling Python to decode stdin as UTF-8 (mojibake for non-ASCII).
        var command = BuildCommand(
            arguments,
            stdOutPipe,
            stdErrPipe,
            standardInput is null ? null : PipeSource.FromString(standardInput, Encoding.UTF8));

        return await ExecuteCoreAsync(
                command, CommandLineFor(arguments), stdOutBuffer, stdErrBuffer,
                new Stopwatch(), killCts: null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<PythonOutputLine> StreamAsync(
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var run = Start(arguments, standardInput, cancellationToken);

        await foreach (var line in run.Lines.WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return line;

        // No result object goes back to the caller in stream mode, so failure must surface
        // here: non-zero exit / timeout throws after the last line has been yielded.
        var result = await run.Completion.ConfigureAwait(false);
        result.EnsureSuccess();
    }

    /// <inheritdoc />
    public PythonRun Start(
        IReadOnlyList<string> arguments,
        string? standardInput = null,
        CancellationToken cancellationToken = default)
        => StartCore(
            arguments,
            standardInput is null ? null : PipeSource.FromString(standardInput, Encoding.UTF8),
            liveInput: null,
            cancellationToken);

    /// <inheritdoc />
    public PythonRun StartInteractive(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
        => StartCore(
            arguments,
            fixedInput: null,
            liveInput: Channel.CreateUnbounded<string>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
            }),
            cancellationToken);

    // ------------------------------------------------------------------------------------
    //  Core plumbing
    // ------------------------------------------------------------------------------------

    private PythonRun StartCore(
        IReadOnlyList<string> arguments,
        PipeSource? fixedInput,
        Channel<string>? liveInput,
        CancellationToken cancellationToken)
    {
        var output = Channel.CreateUnbounded<PythonOutputLine>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false, // stdout and stderr pipes write concurrently
        });

        var stopwatch = new Stopwatch();
        var stdOutBuffer = new StringBuilder();
        var stdErrBuffer = new StringBuilder();

        // Buffer everything (so Completion carries full output) AND fan each line out to the
        // channel with an arrival timestamp (so Lines streams it live).
        var stdOutPipe = PipeTarget.Merge(
            PipeTarget.ToStringBuilder(stdOutBuffer),
            PipeTarget.ToDelegate(line =>
                output.Writer.TryWrite(new PythonOutputLine(PythonOutputSource.StandardOutput, line, stopwatch.Elapsed))));

        var stdErrPipe = PipeTarget.Merge(
            PipeTarget.ToStringBuilder(stdErrBuffer),
            PipeTarget.ToDelegate(line =>
                output.Writer.TryWrite(new PythonOutputLine(PythonOutputSource.StandardError, line, stopwatch.Elapsed))));

        var stdinSource = liveInput is not null
            ? PipeSource.Create((destination, ct) => PumpLiveInputAsync(liveInput.Reader, destination, ct))
            : fixedInput;

        var command = BuildCommand(arguments, stdOutPipe, stdErrPipe, stdinSource);
        var killCts = new CancellationTokenSource();

        var completion = ExecuteAndFinishAsync(
            command, CommandLineFor(arguments), stdOutBuffer, stdErrBuffer,
            stopwatch, output.Writer, liveInput?.Writer, killCts, cancellationToken);

        return new PythonRun(output.Reader, liveInput?.Writer, completion, killCts);
    }

    private async Task<PythonResult> ExecuteAndFinishAsync(
        Command command,
        string commandLine,
        StringBuilder stdOutBuffer,
        StringBuilder stdErrBuffer,
        Stopwatch stopwatch,
        ChannelWriter<PythonOutputLine> outputWriter,
        ChannelWriter<string>? liveInputWriter,
        CancellationTokenSource killCts,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await ExecuteCoreAsync(
                    command, commandLine, stdOutBuffer, stdErrBuffer, stopwatch, killCts, cancellationToken)
                .ConfigureAwait(false);

            outputWriter.TryComplete();
            return result;
        }
        catch (Exception ex)
        {
            // Surface the failure to anyone enumerating Lines as well as to Completion.
            outputWriter.TryComplete(ex);
            throw;
        }
        finally
        {
            // The process is gone — further WriteInputLineAsync calls should fail fast.
            liveInputWriter?.TryComplete();
        }
    }

    /// <summary>
    /// The single execution path every public method funnels into: runs the command, maps
    /// timeout/kill/cancellation faithfully, and builds the <see cref="PythonResult"/>.
    /// </summary>
    private async Task<PythonResult> ExecuteCoreAsync(
        Command command,
        string commandLine,
        StringBuilder stdOutBuffer,
        StringBuilder stdErrBuffer,
        Stopwatch stopwatch,
        CancellationTokenSource? killCts,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = new CancellationTokenSource();
        using var linkedCts = killCts is null
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token)
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token, killCts.Token);

        if (_options.Timeout is { } timeout)
            timeoutCts.CancelAfter(timeout);

        CommandResult? result = null;
        var timedOut = false;
        var killed = false;

        stopwatch.Start();
        try
        {
            result = await command.ExecuteAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Not the caller's token, so it was ours: an explicit Kill wins over the timeout.
            if (killCts?.IsCancellationRequested == true)
                killed = true;
            else if (timeoutCts.IsCancellationRequested)
                timedOut = true;
            else
                throw; // unexpected — don't swallow
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            // Normalize: surface the caller's own token, not our internal linked one.
            throw new OperationCanceledException("The Python run was cancelled.", ex, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            // CliWrap raises this if the process cannot be terminated after cancellation.
            throw new PythonExecutionException(
                $"The Python process could not be terminated after cancellation or timeout. " +
                $"Command: {commandLine}. Underlying error: {ex.Message}",
                ex);
        }
        catch (Win32Exception ex)
        {
            throw new PythonExecutionException(
                $"Failed to start the Python process '{_options.PythonExecutable}'. " +
                "Make sure Python is installed and the executable name or path is correct. " +
                $"Command: {commandLine}. Underlying error: {ex.Message}",
                ex);
        }
        finally
        {
            stopwatch.Stop();
        }

        var pythonResult = new PythonResult
        {
            Executable = _options.PythonExecutable,
            CommandLine = commandLine,
            StandardOutput = stdOutBuffer.ToString(),
            StandardError = stdErrBuffer.ToString(),
            ExitCode = result?.ExitCode ?? -1,
            RunTime = result?.RunTime ?? stopwatch.Elapsed,
            TimedOut = timedOut,
            WasKilled = killed,
        };

        // An explicit kill is the caller's own doing — KillAsync promises a WasKilled result,
        // so ThrowOnNonZeroExitCode must not turn it into an exception.
        if (_options.ThrowOnNonZeroExitCode && !killed)
            pythonResult.EnsureSuccess();

        return pythonResult;
    }

    /// <summary>
    /// Feeds lines from the live-input channel into the process's stdin, flushing after each
    /// batch so the script sees input immediately. Returns when input is completed (EOF) or
    /// the process goes away.
    /// </summary>
    private static async Task PumpLiveInputAsync(
        ChannelReader<string> reader,
        Stream destination,
        CancellationToken cancellationToken)
    {
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var line))
                {
                    await destination.WriteAsync(Encoding.UTF8.GetBytes(line), cancellationToken).ConfigureAwait(false);
                    await destination.WriteAsync(NewlineBytes, cancellationToken).ConfigureAwait(false);
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The process exited (CliWrap cancels stdin piping) or the run was cancelled.
        }
        catch (IOException)
        {
            // Broken pipe: the process closed stdin or exited mid-write. Its exit code tells the story.
        }
    }

    private Command BuildCommand(
        IReadOnlyList<string> arguments,
        PipeTarget stdOutPipe,
        PipeTarget stdErrPipe,
        PipeSource? standardInput)
    {
        var command = Cli.Wrap(_options.PythonExecutable)
            .WithArguments(arguments)                          // CliWrap escapes each argument correctly
            .WithValidation(CommandResultValidation.None)      // we surface exit codes ourselves
            .WithStandardOutputPipe(stdOutPipe)
            .WithStandardErrorPipe(stdErrPipe);

        if (!string.IsNullOrEmpty(_options.WorkingDirectory))
            command = command.WithWorkingDirectory(_options.WorkingDirectory);

        if (standardInput is not null)
            command = command.WithStandardInputPipe(standardInput);

        var environment = BuildEnvironment();
        if (environment.Count > 0)
            command = command.WithEnvironmentVariables(environment);

        return command;
    }

    private IReadOnlyDictionary<string, string?> BuildEnvironment()
    {
        // Env-var names are case-insensitive on Windows only; Linux/macOS distinguish case.
        var environment = new Dictionary<string, string?>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        // Defaults first, then the caller's explicit variables so they always win.
        if (_options.UseUnbufferedOutput)
            environment["PYTHONUNBUFFERED"] = "1";

        if (_options.UseUtf8Io)
        {
            environment["PYTHONIOENCODING"] = "utf-8";
            environment["PYTHONUTF8"] = "1";
        }

        foreach (var kvp in _options.EnvironmentVariables)
            environment[kvp.Key] = kvp.Value;

        if (_options.AdditionalPathEntries.Count > 0)
        {
            var basePath = environment.TryGetValue("PATH", out var explicitPath) && explicitPath is not null
                ? explicitPath
                : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

            var prepend = string.Join(Path.PathSeparator, _options.AdditionalPathEntries);
            environment["PATH"] = basePath.Length == 0
                ? prepend
                : prepend + Path.PathSeparator + basePath;
        }

        return environment;
    }

    private string CommandLineFor(IReadOnlyList<string> arguments)
        => $"{_options.PythonExecutable} {FormatArgumentsForDisplay(arguments)}";

    private static string FormatArgumentsForDisplay(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        for (var i = 0; i < arguments.Count; i++)
        {
            if (i > 0)
                builder.Append(' ');

            var arg = arguments[i];
            if (arg.Length == 0 || arg.AsSpan().ContainsAny(DisplayQuoteTriggers))
                builder.Append('"').Append(arg.Replace("\"", "\\\"")).Append('"');
            else
                builder.Append(arg);
        }

        return builder.ToString();
    }
}
