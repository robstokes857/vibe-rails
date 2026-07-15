using System.Diagnostics;
using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.VCA.Hooks;

public interface IVcaHookPresenter
{
    Task WritePreflightEventAsync(GitPreflightEvent preflightEvent, CancellationToken cancellationToken) =>
        Task.CompletedTask;

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
    private static readonly string[] SpinnerFrames = ["◐", "◓", "◑", "◒"];
    private readonly VcaHookConsoleOptions _options;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _stepSpinnerCts;
    private Task? _stepSpinnerTask;
    private VcaHookKind _currentHookKind = VcaHookKind.PreCommit;

    public VcaConsoleHookPresenter(VcaHookConsoleOptions options)
    {
        _options = options;
    }

    public async Task WritePreflightEventAsync(
        GitPreflightEvent preflightEvent,
        CancellationToken cancellationToken)
    {
        switch (preflightEvent.Type)
        {
            case GitPreflightEventType.RunStarted:
                await WritePreflightHeaderAsync(preflightEvent);
                break;
            case GitPreflightEventType.StepStarted:
                await StopStepSpinnerAsync();
                if (_options.EnableSpinner)
                {
                    _stepSpinnerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _stepSpinnerTask = SpinStepAsync(preflightEvent, _stepSpinnerCts.Token);
                }
                else
                {
                    await WriteOutputLineAsync($"{StepPrefix(preflightEvent)} → [run] {preflightEvent.Message}");
                }
                break;
            case GitPreflightEventType.StepOutput:
                await StopStepSpinnerAsync();
                await WriteOutputLineAsync($"      {preflightEvent.Message}");
                break;
            case GitPreflightEventType.StepFinished:
                await StopStepSpinnerAsync();
                await WriteOutputLineAsync(
                    $"{StepPrefix(preflightEvent)} {StatusIcon(preflightEvent.Status)} [{StatusLabel(preflightEvent.Status)}] " +
                    $"{preflightEvent.Message} · {FormatMilliseconds(preflightEvent.DurationMs)}");
                break;
            case GitPreflightEventType.RunFinished:
                await StopStepSpinnerAsync();
                if (_currentHookKind is VcaHookKind.CommitMessage or VcaHookKind.AcknowledgeCommitMessage)
                {
                    break;
                }
                await WriteOutputLineAsync("");
                var allowed = preflightEvent.CommitAllowed == true;
                await WriteOutputLineAsync(allowed
                    ? $"✓ [pass] Commit allowed · {FormatMilliseconds(preflightEvent.DurationMs)}"
                    : preflightEvent.Status == GitPreflightStepStatus.Cancelled
                        ? $"! [cancelled] Git preflight cancelled · {FormatMilliseconds(preflightEvent.DurationMs)}"
                        : $"✕ [block] Commit blocked · {FormatMilliseconds(preflightEvent.DurationMs)}");
                break;
        }
    }

    public async Task<T> RunWithProgressAsync<T>(
        VcaHookDisplayInfo displayInfo,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        await WriteHeaderAsync(displayInfo);

        if (!_options.EnableSpinner)
        {
            await WriteOutputLineAsync($"→ [run] {displayInfo.Subtitle}");
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
            await WriteOutputLineAsync($"→ [done] {displayInfo.Title} {status} in {FormatElapsed(started.Elapsed)}.");
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

    public Task WriteSuccessAsync(string message) => WriteOutputLineAsync($"✓ [pass] {message}");

    public Task WriteWarningAsync(string message) => WriteOutputLineAsync($"! [warn] {message}");

    public Task WriteFailureAsync(string message) => WriteOutputLineAsync($"✕ [block] {message}");

    public Task WriteErrorAsync(string message) => WriteErrorLineAsync($"× [error] {message}");

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
        await WriteOutputLineAsync($"VibeRails VCA · Git Guard — {info.Title}");
        await WriteOutputLineAsync("────────────────────────────────────────────────────────────");
        if (!string.IsNullOrWhiteSpace(info.RepositoryPath))
        {
            await WriteOutputLineAsync($"Repository: {info.RepositoryPath}");
        }
        await WriteOutputLineAsync(info.Reason);

        if (info.Files.Count > 0)
        {
            await WriteOutputLineAsync($"Staged files ({info.Files.Count}):");
            foreach (var file in info.Files.Take(8))
            {
                await WriteOutputLineAsync($"  • {file}");
            }

            if (info.Files.Count > 8)
            {
                await WriteOutputLineAsync($"  ... and {info.Files.Count - 8} more");
            }
        }
        else
        {
            await WriteOutputLineAsync("Staged files: none (VCA state will still be checked).");
        }

        if (info.Timeout.HasValue)
        {
            await WriteOutputLineAsync($"Timeout: {FormatElapsed(info.Timeout.Value)}");
        }

        await WriteOutputLineAsync("");
    }

    private async Task WritePreflightHeaderAsync(GitPreflightEvent preflightEvent)
    {
        var details = preflightEvent.Details;
        var kind = details != null && details.TryGetValue("hookKind", out var rawKind)
            ? rawKind switch
            {
                nameof(VcaHookKind.CommitMessage) => "Commit Message",
                nameof(VcaHookKind.AcknowledgeCommitMessage) => "Commit Acknowledgment",
                nameof(VcaHookKind.Preview) => "Preview",
                _ => "Pre-Commit"
            }
            : "Pre-Commit";
        _currentHookKind = Enum.TryParse<VcaHookKind>(
            details != null && details.TryGetValue("hookKind", out var hookKind) ? hookKind : null,
            out var parsedKind)
            ? parsedKind
            : VcaHookKind.PreCommit;

        await WriteOutputLineAsync($"VibeRails · Git Guard — {kind}");
        await WriteOutputLineAsync("────────────────────────────────────────────────────────────");
        if (details != null && details.TryGetValue("repositoryPath", out var repositoryPath))
        {
            await WriteOutputLineAsync($"Repository: {repositoryPath}");
        }

        await WriteOutputLineAsync(preflightEvent.Message);
        if (details != null && details.TryGetValue("stagedFiles", out var stagedFiles))
        {
            foreach (var file in stagedFiles.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(8))
            {
                await WriteOutputLineAsync($"  • {file}");
            }
        }

        await WriteOutputLineAsync("");
    }

    private async Task SpinStepAsync(GitPreflightEvent preflightEvent, CancellationToken cancellationToken)
    {
        var index = 0;
        var started = Stopwatch.StartNew();
        while (!cancellationToken.IsCancellationRequested)
        {
            await _writeLock.WaitAsync(CancellationToken.None);
            try
            {
                _options.Output.Write(
                    $"\r{StepPrefix(preflightEvent)} {SpinnerFrames[index++ % SpinnerFrames.Length]} [run] " +
                    $"{preflightEvent.Message} · {FormatElapsed(started.Elapsed)}");
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

    private async Task StopStepSpinnerAsync()
    {
        if (_stepSpinnerCts == null)
        {
            return;
        }

        await _stepSpinnerCts.CancelAsync();
        if (_stepSpinnerTask != null)
        {
            await AwaitSpinnerSilentlyAsync(_stepSpinnerTask);
        }
        _stepSpinnerCts.Dispose();
        _stepSpinnerCts = null;
        _stepSpinnerTask = null;
        await ClearSpinnerLineAsync();
    }

    private static string StepPrefix(GitPreflightEvent preflightEvent) =>
        $"[{preflightEvent.StepNumber ?? 0}/{preflightEvent.StepCount ?? 0}]";

    private static string StatusIcon(GitPreflightStepStatus status) => status switch
    {
        GitPreflightStepStatus.Passed => "✓",
        GitPreflightStepStatus.Warning => "!",
        GitPreflightStepStatus.Blocked or GitPreflightStepStatus.Error => "✕",
        GitPreflightStepStatus.Skipped => "–",
        GitPreflightStepStatus.Cancelled => "!",
        _ => "→"
    };

    private static string StatusLabel(GitPreflightStepStatus status) => status switch
    {
        GitPreflightStepStatus.Passed => "pass",
        GitPreflightStepStatus.Warning => "warn",
        GitPreflightStepStatus.Blocked => "block",
        GitPreflightStepStatus.Skipped => "skip",
        GitPreflightStepStatus.Cancelled => "cancelled",
        GitPreflightStepStatus.Error => "error",
        _ => "run"
    };

    private static string FormatMilliseconds(long? milliseconds) =>
        FormatElapsed(TimeSpan.FromMilliseconds(milliseconds ?? 0));

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
