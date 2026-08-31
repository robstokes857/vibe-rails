using System.Collections.Immutable;
using System.Diagnostics;

namespace VibeRails.Daemon;

public sealed record DaemonProcessRequest
{
    public DaemonProcessRequest(
        string fileName,
        IEnumerable<string> arguments,
        TimeSpan timeout,
        string? workingDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        FileName = fileName;
        Arguments = arguments.ToImmutableArray();
        Timeout = timeout;
        WorkingDirectory = workingDirectory;
    }

    public string FileName { get; }
    public ImmutableArray<string> Arguments { get; }
    public TimeSpan Timeout { get; }
    public string? WorkingDirectory { get; }
}

public sealed record DaemonProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut = false)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

public interface IDaemonProcessRunner
{
    Task<DaemonProcessResult> RunAsync(
        DaemonProcessRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Executes OS lifecycle tools without a shell and with a bounded lifetime.</summary>
public sealed class DaemonProcessRunner : IDaemonProcessRunner
{
    // After the child was killed (or exited), a grandchild that inherited the redirected pipe
    // handles can keep ReadToEndAsync pending forever. Bound every output await so the runner's
    // bounded-lifetime contract holds even then.
    private static readonly TimeSpan OutputDrainGrace = TimeSpan.FromSeconds(2);

    public async Task<DaemonProcessResult> RunAsync(
        DaemonProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = request.FileName,
                WorkingDirectory = request.WorkingDirectory ?? string.Empty,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        foreach (var argument in request.Arguments)
            process.StartInfo.ArgumentList.Add(argument);

        if (!process.Start())
            throw new InvalidOperationException($"Unable to start '{request.FileName}'.");

        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);

        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Whether this is the internal timeout or caller cancellation, the OS tool must not
            // keep mutating registration state after the runner has returned control.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The timeout/cancellation remains the authoritative result even if cleanup is
                // unavailable (the process may have exited between the wait and the kill).
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // Observe the output tasks (bounded) so a cancelled run leaves no unobserved
                // faults behind, then propagate the caller's cancellation.
                _ = await DrainAsync(stdout).ConfigureAwait(false);
                _ = await DrainAsync(stderr).ConfigureAwait(false);
                throw;
            }

            return new DaemonProcessResult(
                -1,
                await DrainAsync(stdout).ConfigureAwait(false),
                await DrainAsync(stderr).ConfigureAwait(false),
                TimedOut: true);
        }

        return new DaemonProcessResult(
            process.ExitCode,
            await DrainAsync(stdout).ConfigureAwait(false),
            await DrainAsync(stderr).ConfigureAwait(false));
    }

    private static async Task<string> DrainAsync(Task<string> output)
    {
        try
        {
            return await output.WaitAsync(OutputDrainGrace).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return string.Empty;
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }
}
