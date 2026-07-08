using System.Diagnostics;

namespace VibeRails.Services.VCA.Hooks;

public interface IVcaHookPresenter
{
    Task<T> RunWithProgressAsync<T>(
        VcaHookDisplayInfo displayInfo,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken);

    Task WriteValidationOutputAsync(string validationOutput);
    Task WriteSuccessAsync(string message);
    Task WriteWarningAsync(string message);
    Task WriteFailureAsync(string message);
    Task WriteErrorAsync(string message);
    Task<string?> ReadLineAsync(string prompt);
}

public sealed record VcaHookConsoleOptions(
    TextWriter Output,
    TextWriter Error,
    TextReader Input,
    bool EnableSpinner);

public sealed class VcaConsoleHookPresenter : IVcaHookPresenter
{
    private static readonly string[] SpinnerFrames = ["|", "/", "-", "\\"];
    private readonly VcaHookConsoleOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public VcaConsoleHookPresenter(VcaHookConsoleOptions options)
    {
        _options = options;
    }

    public async Task<T> RunWithProgressAsync<T>(
        VcaHookDisplayInfo displayInfo,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await WriteHeaderAsync(displayInfo);

        if (!_options.EnableSpinner)
        {
            await WriteOutputLineAsync($"> {displayInfo.Subtitle}");
            return await operation(cancellationToken);
        }

        using var spinnerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var started = Stopwatch.StartNew();
        var spinner = SpinAsync(displayInfo.Subtitle, started, spinnerCts.Token);

        var completed = false;
        try
        {
            var result = await operation(cancellationToken);
            completed = true;
            return result;
        }
        finally
        {
            await spinnerCts.CancelAsync();
            await AwaitSpinnerSilentlyAsync(spinner);
            await ClearSpinnerLineAsync();
            var status = completed ? "finished" : "stopped";
            await WriteOutputLineAsync($"> {displayInfo.Title} {status} in {FormatElapsed(started.Elapsed)}.");
        }
    }

    public Task WriteValidationOutputAsync(string validationOutput)
    {
        if (string.IsNullOrWhiteSpace(validationOutput))
        {
            return Task.CompletedTask;
        }

        return WriteOutputLineAsync(validationOutput.TrimEnd());
    }

    public Task WriteSuccessAsync(string message) => WriteOutputLineAsync($"[pass] {message}");

    public Task WriteWarningAsync(string message) => WriteOutputLineAsync($"[warn] {message}");

    public Task WriteFailureAsync(string message) => WriteOutputLineAsync($"[block] {message}");

    public Task WriteErrorAsync(string message) => WriteErrorLineAsync($"[error] {message}");

    public async Task<string?> ReadLineAsync(string prompt)
    {
        await _writeLock.WaitAsync(CancellationToken.None);
        try
        {
            await _options.Output.WriteAsync(prompt);
            await _options.Output.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }

        return await _options.Input.ReadLineAsync();
    }

    private async Task WriteHeaderAsync(VcaHookDisplayInfo info)
    {
        await WriteOutputLineAsync($"VibeRails VCA: {info.Title} check");
        await WriteOutputLineAsync("========================================");
        await WriteOutputLineAsync(info.Reason);

        if (info.Files.Count > 0)
        {
            await WriteOutputLineAsync("Triggered by staged files:");
            foreach (var file in info.Files.Take(8))
            {
                await WriteOutputLineAsync($"  - {file}");
            }

            if (info.Files.Count > 8)
            {
                await WriteOutputLineAsync($"  ... and {info.Files.Count - 8} more");
            }
        }
        else
        {
            await WriteOutputLineAsync("No staged files were detected yet; checking VCA state anyway.");
        }

        if (info.Timeout.HasValue)
        {
            await WriteOutputLineAsync($"Timeout: {FormatElapsed(info.Timeout.Value)}");
        }

        await WriteOutputLineAsync("");
    }

    private async Task SpinAsync(string message, Stopwatch started, CancellationToken cancellationToken)
    {
        var index = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await _writeLock.WaitAsync(CancellationToken.None);
            try
            {
                _options.Output.Write($"\r{SpinnerFrames[index++ % SpinnerFrames.Length]} {message}  elapsed {FormatElapsed(started.Elapsed)}");
                await _options.Output.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }

            try
            {
                await Task.Delay(120, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ClearSpinnerLineAsync()
    {
        await _writeLock.WaitAsync(CancellationToken.None);
        try
        {
            _options.Output.Write("\r" + new string(' ', 100) + "\r");
            await _options.Output.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task AwaitSpinnerSilentlyAsync(Task spinner)
    {
        try
        {
            await spinner;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task WriteOutputLineAsync(string message)
    {
        await _writeLock.WaitAsync(CancellationToken.None);
        try
        {
            await _options.Output.WriteLineAsync(message);
            await _options.Output.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteErrorLineAsync(string message)
    {
        await _writeLock.WaitAsync(CancellationToken.None);
        try
        {
            await _options.Error.WriteLineAsync(message);
            await _options.Error.FlushAsync();
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalHours >= 1
            ? elapsed.ToString(@"h\:mm\:ss")
            : elapsed.ToString(@"m\:ss");
}
