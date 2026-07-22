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
    bool EnableSpinner,
    VcaConsoleStyle? Style = null);

public sealed class VcaConsoleHookPresenter : IVcaHookPresenter
{
    // Width of the banner box interior and the verdict bars; matches the historical
    // 60-character plain separator so both layouts line up in the same window.
    private const int BannerInnerWidth = 60;
    private const string Brand = "VibeRails · Git Guard";

    // The brand gradient endpoints (cyan → magenta) used for the banner text, the
    // rail-spinner dot, and other truecolor accents.
    private static readonly (int R, int G, int B) BrandFrom = (0, 229, 255);
    private static readonly (int R, int G, int B) BrandTo = (255, 64, 224);

    // Width in cells of the animated rail the spinner dot rides along.
    private const int RailWidth = 14;

    private static readonly string[] PlainSpinnerFrames = ["◐", "◓", "◑", "◒"];

    private readonly VcaHookConsoleOptions _options;
    private readonly VcaConsoleStyle _style;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _stepSpinnerCts;
    private Task? _stepSpinnerTask;
    private VcaHookKind _currentHookKind = VcaHookKind.PreCommit;

    public VcaConsoleHookPresenter(VcaHookConsoleOptions options)
    {
        _options = options;
        _style = options.Style ?? VcaConsoleStyle.Plain;
    }

    private int SpinnerIntervalMs => _style.Enabled ? 70 : 120;

    /// <summary>
    /// One animation frame: styled mode gets a glowing dot bouncing along a rail
    /// (gradient-colored by position); plain mode keeps the classic rotating disc.
    /// </summary>
    private string SpinnerFrame(int index)
    {
        if (!_style.Enabled)
        {
            return PlainSpinnerFrames[index % PlainSpinnerFrames.Length];
        }

        var period = 2 * (RailWidth - 1);
        var position = index % period;
        if (position >= RailWidth)
        {
            position = period - position;
        }

        var frame = new System.Text.StringBuilder();
        frame.Append($"{_style.Dim}╞{_style.Reset}");
        for (var cell = 0; cell < RailWidth; cell++)
        {
            if (cell == position)
            {
                var t = (double)cell / (RailWidth - 1);
                frame.Append(_style.Fg(
                    Lerp(BrandFrom.R, BrandTo.R, t),
                    Lerp(BrandFrom.G, BrandTo.G, t),
                    Lerp(BrandFrom.B, BrandTo.B, t)));
                frame.Append('●');
                frame.Append(_style.Reset);
            }
            else
            {
                frame.Append($"{_style.Dim}═{_style.Reset}");
            }
        }
        frame.Append($"{_style.Dim}╡{_style.Reset}");
        return frame.ToString();
    }

    /// <summary>Per-character truecolor gradient across <paramref name="text"/>.</summary>
    private string GradientText(string text, (int R, int G, int B) from, (int R, int G, int B) to)
    {
        if (!_style.Enabled || text.Length == 0)
        {
            return text;
        }

        var result = new System.Text.StringBuilder();
        var last = Math.Max(1, text.Length - 1);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != ' ')
            {
                var t = (double)i / last;
                result.Append(_style.Fg(
                    Lerp(from.R, to.R, t),
                    Lerp(from.G, to.G, t),
                    Lerp(from.B, to.B, t)));
            }
            result.Append(text[i]);
        }
        result.Append(_style.Reset);
        return result.ToString();
    }

    /// <summary>Full-width verdict bar with a horizontal background gradient.</summary>
    private string GradientBar(string text, (int R, int G, int B) from, (int R, int G, int B) to, string foreground)
    {
        var padded = text.PadRight(BannerInnerWidth + 2);
        if (!_style.Enabled)
        {
            return padded;
        }

        var result = new System.Text.StringBuilder(foreground);
        var last = Math.Max(1, padded.Length - 1);
        for (var i = 0; i < padded.Length; i++)
        {
            var t = (double)i / last;
            result.Append(_style.Bg(
                Lerp(from.R, to.R, t),
                Lerp(from.G, to.G, t),
                Lerp(from.B, to.B, t)));
            result.Append(padded[i]);
        }
        result.Append(_style.Reset);
        return result.ToString();
    }

    private static int Lerp(int from, int to, double t) => (int)Math.Round(from + (to - from) * t);

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
                    await WriteOutputLineAsync(
                        $"{_style.Dim}{StepPrefix(preflightEvent)}{_style.Reset} " +
                        $"{_style.Cyan}→ [run]{_style.Reset} {preflightEvent.Message}");
                }
                break;
            case GitPreflightEventType.StepOutput:
                await StopStepSpinnerAsync();
                await WriteOutputLineAsync($"      {DecorateTranscriptLine(preflightEvent.Message)}");
                break;
            case GitPreflightEventType.StepFinished:
                await StopStepSpinnerAsync();
                var stepColor = StatusColor(preflightEvent.Status);
                await WriteOutputLineAsync(
                    $"{_style.Dim}{StepPrefix(preflightEvent)}{_style.Reset} " +
                    $"{stepColor}{StatusIcon(preflightEvent.Status)} [{StatusLabel(preflightEvent.Status)}]{_style.Reset} " +
                    $"{preflightEvent.Message} " +
                    $"{_style.Dim}· {FormatMilliseconds(preflightEvent.DurationMs)}{_style.Reset}");
                break;
            case GitPreflightEventType.RunFinished:
                await StopStepSpinnerAsync();
                if (_currentHookKind is VcaHookKind.CommitMessage or VcaHookKind.AcknowledgeCommitMessage)
                {
                    break;
                }
                await WriteOutputLineAsync("");
                await WriteRunVerdictAsync(preflightEvent);
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
            await WriteOutputLineAsync(
                $"{_style.Cyan}→ [done]{_style.Reset} {displayInfo.Title} {status} in " +
                $"{FormatElapsed(started.Elapsed)}.");
        }
    }

    public async Task WriteValidationOutputAsync(string validationOutput)
    {
        if (string.IsNullOrWhiteSpace(validationOutput))
        {
            return;
        }

        foreach (var line in validationOutput.TrimEnd().Split('\n'))
        {
            await WriteOutputLineAsync(DecorateTranscriptLine(line.TrimEnd('\r')));
        }
    }

    public Task WriteSuccessAsync(string message) =>
        WriteOutputLineAsync($"{_style.Green}✓ [pass]{_style.Reset} {message}");

    public Task WriteWarningAsync(string message) =>
        WriteOutputLineAsync($"{_style.Yellow}! [warn]{_style.Reset} {message}");

    public Task WriteFailureAsync(string message) =>
        WriteOutputLineAsync($"{_style.Red}✕ [block]{_style.Reset} {message}");

    public Task WriteErrorAsync(string message) =>
        WriteErrorLineAsync($"{_style.Red}× [error]{_style.Reset} {message}");

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

    private async Task WriteRunVerdictAsync(GitPreflightEvent preflightEvent)
    {
        var allowed = preflightEvent.CommitAllowed == true;
        var duration = FormatMilliseconds(preflightEvent.DurationMs);

        if (!_style.Enabled)
        {
            await WriteOutputLineAsync(allowed
                ? $"✓ [pass] Commit allowed · {duration}"
                : preflightEvent.Status == GitPreflightStepStatus.Cancelled
                    ? $"! [cancelled] Git preflight cancelled · {duration}"
                    : $"✕ [block] Commit blocked · {duration}");
            return;
        }

        var black = _style.Fg(10, 20, 10) + _style.Bold;
        var white = _style.Fg(255, 255, 255) + _style.Bold;
        var line = allowed
            ? GradientBar($"  ✓  COMMIT ALLOWED · {duration}", (0, 110, 60), (60, 235, 120), black)
            : preflightEvent.Status == GitPreflightStepStatus.Cancelled
                ? GradientBar($"  !  PREFLIGHT CANCELLED · {duration}", (150, 110, 0), (255, 210, 60), black)
                : GradientBar($"  ✕  COMMIT BLOCKED · {duration}", (130, 15, 25), (255, 75, 85), white);
        await WriteOutputLineAsync(line);
    }

    private async Task WriteHeaderAsync(VcaHookDisplayInfo info)
    {
        if (_style.Enabled)
        {
            await WriteBannerAsync(info.Title);
        }
        else
        {
            await WriteOutputLineAsync($"VibeRails VCA · Git Guard — {info.Title}");
            await WriteOutputLineAsync(new string('─', BannerInnerWidth));
        }

        if (!string.IsNullOrWhiteSpace(info.RepositoryPath))
        {
            await WriteOutputLineAsync($"{_style.Dim}Repository:{_style.Reset} {info.RepositoryPath}");
        }
        await WriteOutputLineAsync(info.Reason);

        if (info.Files.Count > 0)
        {
            await WriteOutputLineAsync($"{_style.Dim}Staged files ({info.Files.Count}):{_style.Reset}");
            foreach (var file in info.Files.Take(8))
            {
                await WriteOutputLineAsync($"  {_style.Cyan}•{_style.Reset} {file}");
            }

            if (info.Files.Count > 8)
            {
                await WriteOutputLineAsync($"  {_style.Dim}... and {info.Files.Count - 8} more{_style.Reset}");
            }
        }
        else
        {
            await WriteOutputLineAsync("Staged files: none (VCA state will still be checked).");
        }

        if (info.Timeout.HasValue)
        {
            await WriteOutputLineAsync($"{_style.Dim}Timeout:{_style.Reset} {FormatElapsed(info.Timeout.Value)}");
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

        if (_style.Enabled)
        {
            await WriteBannerAsync(kind);
        }
        else
        {
            await WriteOutputLineAsync($"{Brand} — {kind}");
            await WriteOutputLineAsync(new string('─', BannerInnerWidth));
        }

        if (details != null && details.TryGetValue("repositoryPath", out var repositoryPath))
        {
            await WriteOutputLineAsync($"{_style.Dim}Repository:{_style.Reset} {repositoryPath}");
        }

        await WriteOutputLineAsync(preflightEvent.Message);
        if (details != null && details.TryGetValue("stagedFiles", out var stagedFiles))
        {
            foreach (var file in stagedFiles.Split('\n', StringSplitOptions.RemoveEmptyEntries).Take(8))
            {
                await WriteOutputLineAsync($"  {_style.Cyan}•{_style.Reset} {file}");
            }
        }

        await WriteOutputLineAsync("");
    }

    private async Task WriteBannerAsync(string kind)
    {
        var padding = Math.Max(1, BannerInnerWidth - 4 - Brand.Length - kind.Length);
        await WriteOutputLineAsync($"{_style.Dim}╭{new string('─', BannerInnerWidth)}╮{_style.Reset}");
        await WriteOutputLineAsync(
            $"{_style.Dim}│{_style.Reset}  {_style.Bold}{GradientText(Brand, BrandFrom, BrandTo)}" +
            $"{new string(' ', padding)}{_style.Bold}{_style.Magenta}{kind}{_style.Reset}  " +
            $"{_style.Dim}│{_style.Reset}");
        await WriteOutputLineAsync($"{_style.Dim}╰{new string('─', BannerInnerWidth)}╯{_style.Reset}");
    }

    /// <summary>
    /// Color-hints well-known VCA transcript prefixes (PASS/WARN/STOP/ERROR) so the
    /// validation output reads at a glance. Plain mode returns the line untouched.
    /// </summary>
    private string DecorateTranscriptLine(string line)
    {
        if (!_style.Enabled)
        {
            return line;
        }

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("PASS", StringComparison.Ordinal))
        {
            return $"{_style.Green}{line}{_style.Reset}";
        }
        if (trimmed.StartsWith("[WARN]", StringComparison.Ordinal) ||
            trimmed.StartsWith("WARN", StringComparison.Ordinal))
        {
            return $"{_style.Yellow}{line}{_style.Reset}";
        }
        if (trimmed.StartsWith("[STOP]", StringComparison.Ordinal) ||
            trimmed.StartsWith("STOP", StringComparison.Ordinal) ||
            trimmed.StartsWith("ERROR", StringComparison.Ordinal))
        {
            return $"{_style.Red}{line}{_style.Reset}";
        }

        return line;
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
                    $"\r{_style.ClearLine}{_style.Dim}{StepPrefix(preflightEvent)}{_style.Reset} " +
                    $"{SpinnerFrame(index++)} [run] " +
                    $"{preflightEvent.Message} {_style.Dim}· {FormatElapsed(started.Elapsed)}{_style.Reset}");
                await _options.Output.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }

            try
            {
                await Task.Delay(SpinnerIntervalMs, cancellationToken);
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

    private string StatusColor(GitPreflightStepStatus status) => status switch
    {
        GitPreflightStepStatus.Passed => _style.Green,
        GitPreflightStepStatus.Warning or GitPreflightStepStatus.Cancelled => _style.Yellow,
        GitPreflightStepStatus.Blocked or GitPreflightStepStatus.Error => _style.Red,
        GitPreflightStepStatus.Skipped => _style.Dim,
        _ => _style.Cyan
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
                _options.Output.Write(
                    $"\r{_style.ClearLine}{SpinnerFrame(index++)} " +
                    $"{message}  {_style.Dim}elapsed {FormatElapsed(started.Elapsed)}{_style.Reset}");
                await _options.Output.FlushAsync();
            }
            finally
            {
                _writeLock.Release();
            }

            try
            {
                await Task.Delay(SpinnerIntervalMs, cancellationToken);
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
            _options.Output.Write(_style.Enabled
                ? $"\r{_style.ClearLine}"
                : "\r" + new string(' ', 100) + "\r");
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
