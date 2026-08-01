using System.Diagnostics;
using System.Text;
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
    VcaConsoleStyle? Style = null,
    // Console columns to lay out against. Null asks the console itself; the fallback keeps
    // rendering sane when there is no console to ask (tests, capture harnesses).
    int? Width = null);

/// <summary>
/// Renders the Git Guard hook transcript.
///
/// Styled mode is a progress list, not a log: a step shows only its name and a spinner while it
/// runs, and its output is held back until the verdict is in. A step that passed collapses to one
/// muted line with a green check — nothing that passed is worth the reader's attention. Only a
/// step that warned or blocked expands, and then it expands fully: one entry per finding, tagged
/// by severity, wrapped to the window instead of spilling into the left margin.
///
/// Plain mode is the historical line-by-line transcript, byte for byte. It is what redirected
/// consumers (tests, the Rules page validator, VS Code's SCM log) read, and reordering output
/// there would change their meaning, so every layout decision below is gated on
/// <see cref="VcaConsoleStyle.Enabled"/>.
/// </summary>
public sealed class VcaConsoleHookPresenter : IVcaHookPresenter
{
    // Width of the banner box interior and the verdict bars in plain mode; matches the historical
    // 60-character plain separator so both layouts line up in the same window.
    private const int BannerInnerWidth = 60;
    private const string Brand = "VibeRails · Git Guard";

    // The brand gradient endpoints (cyan → magenta) used for the banner text and other
    // truecolor accents.
    private static readonly (int R, int G, int B) BrandFrom = (0, 229, 255);
    private static readonly (int R, int G, int B) BrandTo = (255, 64, 224);

    // Layout columns for the styled progress list. Bodies hang under the step label, and a
    // finding's prose hangs under its severity tag, so no wrapped line ever starts at column 0.
    private const int Gutter = 2;
    private const int BodyIndent = 8;
    private const int FindingIndent = 11;
    private const int MinLineWidth = 64;
    private const int MaxLineWidth = 104;
    private const int FallbackConsoleWidth = 100;

    private static readonly string[] PlainSpinnerFrames = ["◐", "◓", "◑", "◒"];
    private static readonly string[] StyledSpinnerFrames =
        ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private readonly VcaHookConsoleOptions _options;
    private readonly VcaConsoleStyle _style;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private CancellationTokenSource? _stepSpinnerCts;
    private Task? _stepSpinnerTask;
    private VcaHookKind _currentHookKind = VcaHookKind.PreCommit;
    private int? _contentWidth;

    // Styled mode holds a step's transcript until the step finishes, so the spinner stays alone
    // on its line and a passing step can drop its output entirely.
    private readonly List<string> _stepOutput = [];
    private string _currentStepName = "";

    public VcaConsoleHookPresenter(VcaHookConsoleOptions options)
    {
        _options = options;
        _style = options.Style ?? VcaConsoleStyle.Plain;
    }

    private int SpinnerIntervalMs => _style.Enabled ? 80 : 120;

    /// <summary>
    /// Total printable width every styled line is laid out against, gutter included, so the
    /// banner, the step rows and the verdict bar all share one right edge. Capped well below a
    /// maximised window: prose set to 150+ columns is harder to read, not easier.
    /// </summary>
    private int LineWidth => _contentWidth ??= Math.Clamp(
        (_options.Width ?? TryGetConsoleWidth() ?? FallbackConsoleWidth) - 1,
        MinLineWidth,
        MaxLineWidth);

    private static int? TryGetConsoleWidth()
    {
        try
        {
            var width = Console.WindowWidth;
            return width > 20 ? width : null;
        }
        catch
        {
            // No console attached (redirected output, service host). The caller falls back.
            return null;
        }
    }

    private string SpinnerFrame(int index) => _style.Enabled
        ? StyledSpinnerFrames[index % StyledSpinnerFrames.Length]
        : PlainSpinnerFrames[index % PlainSpinnerFrames.Length];

    /// <summary>Per-character truecolor gradient across <paramref name="text"/>.</summary>
    private string GradientText(string text, (int R, int G, int B) from, (int R, int G, int B) to)
    {
        if (!_style.Enabled || text.Length == 0)
        {
            return text;
        }

        var result = new StringBuilder();
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
    private string GradientBar(
        string text,
        (int R, int G, int B) from,
        (int R, int G, int B) to,
        string foreground,
        int width)
    {
        var padded = text.PadRight(width);
        if (!_style.Enabled)
        {
            return padded;
        }

        var result = new StringBuilder(foreground);
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

    public Task WritePreflightEventAsync(
        GitPreflightEvent preflightEvent,
        CancellationToken cancellationToken) =>
        _style.Enabled
            ? WriteStyledEventAsync(preflightEvent, cancellationToken)
            : WritePlainEventAsync(preflightEvent, cancellationToken);

    // ---------------------------------------------------------------- styled progress list

    private async Task WriteStyledEventAsync(
        GitPreflightEvent preflightEvent,
        CancellationToken cancellationToken)
    {
        switch (preflightEvent.Type)
        {
            case GitPreflightEventType.RunStarted:
                await WriteStyledHeaderAsync(preflightEvent);
                break;

            case GitPreflightEventType.StepStarted:
                await StopStepSpinnerAsync();
                _stepOutput.Clear();
                _currentStepName = preflightEvent.Message;
                if (_options.EnableSpinner)
                {
                    _stepSpinnerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    _stepSpinnerTask = SpinStepAsync(preflightEvent, _stepSpinnerCts.Token);
                }
                break;

            // Held back on purpose: the spinner keeps the line to itself, and whether any of
            // this is worth showing is not known until the step reports its status.
            case GitPreflightEventType.StepOutput:
                _stepOutput.Add(preflightEvent.Message);
                break;

            case GitPreflightEventType.StepFinished:
                await StopStepSpinnerAsync();
                await WriteStyledStepResultAsync(preflightEvent);
                _stepOutput.Clear();
                break;

            case GitPreflightEventType.RunFinished:
                await StopStepSpinnerAsync();
                if (_currentHookKind is VcaHookKind.CommitMessage or VcaHookKind.AcknowledgeCommitMessage)
                {
                    break;
                }
                await WriteOutputLineAsync("");
                await WriteRunVerdictAsync(preflightEvent);
                await WriteOutputLineAsync("");
                break;
        }
    }

    private async Task WriteStyledHeaderAsync(GitPreflightEvent preflightEvent)
    {
        var details = preflightEvent.Details;
        _currentHookKind = ParseHookKind(details);
        var kind = DescribeHookKind(_currentHookKind);

        await WriteOutputLineAsync("");
        await WriteBannerAsync(kind);

        var meta = new List<string>();
        if (details != null && details.TryGetValue("repositoryPath", out var repositoryPath))
        {
            meta.Add(repositoryPath);
        }
        // The file list itself is deliberately not printed: `git status` already answers "what is
        // staged", and 71 bullet points ahead of the actual findings is what buried them before.
        meta.Add(details != null && details.TryGetValue("stagedFileCount", out var stagedFileCount)
            ? $"{stagedFileCount} staged file(s)"
            : preflightEvent.Message.TrimEnd('.'));

        await WriteOutputLineAsync(
            $"{Indent(Gutter)}{_style.Dim}{string.Join(" · ", meta)}{_style.Reset}");
        await WriteOutputLineAsync("");
    }

    /// <summary>
    /// One line per step. Passed and skipped steps stop there — muted, checked, done. Anything
    /// else expands into its summary and the detail the step produced.
    /// </summary>
    private async Task WriteStyledStepResultAsync(GitPreflightEvent preflightEvent)
    {
        var status = preflightEvent.Status;
        var quiet = status is GitPreflightStepStatus.Passed or GitPreflightStepStatus.Skipped;
        var severe = status is GitPreflightStepStatus.Blocked or GitPreflightStepStatus.Error;
        var label = string.IsNullOrWhiteSpace(_currentStepName)
            ? preflightEvent.Message
            : _currentStepName;

        await WriteOutputLineAsync(BuildStepLine(
            preflightEvent,
            StyledStatusIcon(status),
            StatusColor(status),
            label,
            labelStyle: quiet ? _style.Dim : severe ? _style.Bold : "",
            status == GitPreflightStepStatus.Skipped
                ? "skipped"
                : FormatMilliseconds(preflightEvent.DurationMs)));

        var findings = preflightEvent.VcaSummary?.Findings;
        if (quiet)
        {
            // A pass says everything except this: a rule that a later hook will evaluate has not
            // actually been checked yet, and silently swallowing that would overstate the pass.
            await WriteDeferredNoteAsync(findings);
            return;
        }

        await WriteOutputLineAsync(
            $"{Indent(BodyIndent)}{StatusColor(status)}{preflightEvent.Message}{_style.Reset}");

        if (findings is { Count: > 0 })
        {
            await WriteFindingsAsync(findings);
        }
        else
        {
            await WriteBufferedStepOutputAsync();
        }

        await WriteOutputLineAsync("");
    }

    /// <summary>One muted line naming the rules a later hook still has to evaluate.</summary>
    private async Task WriteDeferredNoteAsync(IReadOnlyList<VcaRuleFinding>? findings)
    {
        var deferred = findings?
            .Where(finding => finding.Kind == VcaRuleFindingKind.Deferred)
            .Select(finding => finding.Rule)
            .ToList();
        if (deferred is not { Count: > 0 })
        {
            return;
        }

        foreach (var line in WrapToIndent(
            $"deferred to the commit-message hook · {string.Join(", ", deferred)}",
            BodyIndent,
            _style.Dim))
        {
            await WriteOutputLineAsync(line);
        }
    }

    /// <summary>
    /// Findings the VCA step reported, worst first. These arrive structured, so each one gets a
    /// severity tag, its rule, the reason it fired and what to do about it — rather than the flat
    /// paragraph the same information occupies in the transcript.
    /// </summary>
    private async Task WriteFindingsAsync(IReadOnlyList<VcaRuleFinding> findings)
    {
        foreach (var finding in findings.OrderBy(FindingSeverityRank))
        {
            await WriteOutputLineAsync("");
            var (icon, tag, color, bold) = DescribeFinding(finding.Kind);
            await WriteOutputLineAsync(
                $"{Indent(BodyIndent)}{color}{icon}{_style.Reset}  " +
                $"{color}{_style.Bold}{tag}{_style.Reset} {_style.Dim}·{_style.Reset} " +
                $"{(bold ? _style.Bold : "")}{finding.Rule}{_style.Reset}");

            foreach (var line in WrapToIndent(finding.Reason, FindingIndent))
            {
                await WriteOutputLineAsync(line);
            }

            if (!string.IsNullOrWhiteSpace(finding.Acknowledgment))
            {
                await WriteOutputLineAsync(
                    $"{Indent(FindingIndent)}{_style.Dim}acknowledge with{_style.Reset} " +
                    $"{_style.Cyan}{finding.Acknowledgment} Reason: <your explanation>{_style.Reset}");
            }

            // Guidance repeats what a WARN or deferred check already implies; it only earns its
            // lines when the finding is actually standing between the user and a commit.
            if (finding.Kind is VcaRuleFindingKind.Blocked or VcaRuleFindingKind.AcknowledgmentRequired
                && !string.IsNullOrWhiteSpace(finding.Guidance))
            {
                foreach (var line in WrapToIndent(
                    $"→ {finding.Guidance}",
                    FindingIndent,
                    _style.Dim,
                    hanging: 2))
                {
                    await WriteOutputLineAsync(line);
                }
            }
        }
    }

    /// <summary>
    /// Fallback for steps that report prose rather than findings (MintLint). Each line keeps its
    /// own relative indent and wraps under itself instead of running off the right edge.
    /// </summary>
    private async Task WriteBufferedStepOutputAsync()
    {
        if (_stepOutput.Count == 0)
        {
            return;
        }

        await WriteOutputLineAsync("");
        foreach (var raw in _stepOutput)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                continue;
            }

            var ownIndent = line.Length - line.TrimStart().Length;
            foreach (var wrapped in WrapToIndent(
                line.Trim(),
                BodyIndent + ownIndent,
                DecorationFor(line),
                hanging: 2))
            {
                await WriteOutputLineAsync(wrapped);
            }
        }
    }

    private string BuildStepLine(
        GitPreflightEvent preflightEvent,
        string icon,
        string iconColor,
        string label,
        string labelStyle,
        string trailing)
    {
        var prefix = StepPrefix(preflightEvent);
        // Icons and spinner frames are all single-cell, so the plain width is countable up front.
        var used = Gutter + prefix.Length + 1 + 1 + 2 + label.Length;
        var padding = Math.Max(1, LineWidth - used - trailing.Length);
        return $"{Indent(Gutter)}{_style.Dim}{prefix}{_style.Reset} " +
            $"{iconColor}{icon}{_style.Reset}  " +
            $"{labelStyle}{label}{_style.Reset}{new string(' ', padding)}" +
            $"{_style.Dim}{trailing}{_style.Reset}";
    }

    /// <summary>
    /// Wraps <paramref name="text"/> into the column starting at <paramref name="indent"/>, so a
    /// long reason continues under itself instead of restarting at column 0.
    /// </summary>
    private IEnumerable<string> WrapToIndent(string text, int indent, string style = "", int hanging = 0)
    {
        var width = Math.Max(24, LineWidth - indent - hanging);
        var first = true;
        foreach (var line in WrapText(text, width))
        {
            var column = first ? indent : indent + hanging;
            first = false;
            yield return $"{Indent(column)}{style}{line}{(style.Length > 0 ? _style.Reset : "")}";
        }
    }

    internal static IEnumerable<string> WrapText(string text, int width)
    {
        var normalized = text.Replace('\t', ' ').Trim();
        if (normalized.Length == 0)
        {
            yield break;
        }

        var line = new StringBuilder();
        foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = word;
            // A single token longer than the column (a path, a token) is broken rather than
            // allowed to push the line past the window edge.
            while (candidate.Length > width)
            {
                if (line.Length > 0)
                {
                    yield return line.ToString();
                    line.Clear();
                }
                yield return candidate[..width];
                candidate = candidate[width..];
            }

            if (line.Length > 0 && line.Length + 1 + candidate.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }
            line.Append(candidate);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    private static string Indent(int count) => new(' ', count);

    private static int FindingSeverityRank(VcaRuleFinding finding) => finding.Kind switch
    {
        VcaRuleFindingKind.Blocked => 0,
        VcaRuleFindingKind.AcknowledgmentRequired => 1,
        VcaRuleFindingKind.Warning => 2,
        _ => 3
    };

    private (string Icon, string Tag, string Color, bool Bold) DescribeFinding(VcaRuleFindingKind kind) =>
        kind switch
        {
            VcaRuleFindingKind.Blocked => ("✕", "STOP", _style.Red, true),
            VcaRuleFindingKind.AcknowledgmentRequired => ("!", "COMMIT", _style.Yellow, true),
            VcaRuleFindingKind.Warning => ("▲", "WARN", _style.Yellow, false),
            _ => ("·", "LATER", _style.Cyan, false)
        };

    private static string StyledStatusIcon(GitPreflightStepStatus status) => status switch
    {
        GitPreflightStepStatus.Passed => "✓",
        GitPreflightStepStatus.Warning => "▲",
        GitPreflightStepStatus.Blocked or GitPreflightStepStatus.Error => "✕",
        GitPreflightStepStatus.Skipped => "·",
        GitPreflightStepStatus.Cancelled => "▲",
        _ => "›"
    };

    // ---------------------------------------------------------------- plain transcript

    private async Task WritePlainEventAsync(
        GitPreflightEvent preflightEvent,
        CancellationToken cancellationToken)
    {
        switch (preflightEvent.Type)
        {
            case GitPreflightEventType.RunStarted:
                await WritePlainHeaderAsync(preflightEvent);
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
                        $"{StepPrefix(preflightEvent)} → [run] {preflightEvent.Message}");
                }
                break;
            case GitPreflightEventType.StepOutput:
                await StopStepSpinnerAsync();
                await WriteOutputLineAsync($"      {preflightEvent.Message}");
                break;
            case GitPreflightEventType.StepFinished:
                await StopStepSpinnerAsync();
                await WriteOutputLineAsync(
                    $"{StepPrefix(preflightEvent)} " +
                    $"{StatusIcon(preflightEvent.Status)} [{StatusLabel(preflightEvent.Status)}] " +
                    $"{preflightEvent.Message} " +
                    $"· {FormatMilliseconds(preflightEvent.DurationMs)}");
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

    private async Task WritePlainHeaderAsync(GitPreflightEvent preflightEvent)
    {
        var details = preflightEvent.Details;
        _currentHookKind = ParseHookKind(details);
        await WriteOutputLineAsync($"{Brand} — {DescribeHookKind(_currentHookKind)}");
        await WriteOutputLineAsync(new string('─', BannerInnerWidth));

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

    // ---------------------------------------------------------------- shared

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

    public Task WriteSuccessAsync(string message) => _style.Enabled
        ? WriteOutputLineAsync($"{Indent(Gutter)}{_style.Green}✓{_style.Reset}  {message}")
        : WriteOutputLineAsync($"✓ [pass] {message}");

    public Task WriteWarningAsync(string message) => _style.Enabled
        ? WriteOutputLineAsync($"{Indent(Gutter)}{_style.Yellow}▲{_style.Reset}  {message}")
        : WriteOutputLineAsync($"! [warn] {message}");

    public Task WriteFailureAsync(string message) => _style.Enabled
        ? WriteOutputLineAsync($"{Indent(Gutter)}{_style.Red}✕{_style.Reset}  {_style.Bold}{message}{_style.Reset}")
        : WriteOutputLineAsync($"✕ [block] {message}");

    public Task WriteErrorAsync(string message) => _style.Enabled
        ? WriteErrorLineAsync($"{Indent(Gutter)}{_style.Red}✕{_style.Reset}  {_style.Bold}{message}{_style.Reset}")
        : WriteErrorLineAsync($"× [error] {message}");

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
        var cancelled = preflightEvent.Status == GitPreflightStepStatus.Cancelled;
        var duration = FormatMilliseconds(preflightEvent.DurationMs);

        if (!_style.Enabled)
        {
            await WriteOutputLineAsync(allowed
                ? $"✓ [pass] Commit allowed · {duration}"
                : cancelled
                    ? $"! [cancelled] Git preflight cancelled · {duration}"
                    : $"✕ [block] Commit blocked · {duration}");
            return;
        }

        var black = _style.Fg(10, 20, 10) + _style.Bold;
        var white = _style.Fg(255, 255, 255) + _style.Bold;
        var line = allowed
            ? GradientBar($"  ✓  COMMIT ALLOWED · {duration}", (0, 110, 60), (60, 235, 120), black, LineWidth - Gutter)
            : cancelled
                ? GradientBar($"  ▲  PREFLIGHT CANCELLED · {duration}", (150, 110, 0), (255, 210, 60), black, LineWidth - Gutter)
                : GradientBar($"  ✕  COMMIT BLOCKED · {duration}", (130, 15, 25), (255, 75, 85), white, LineWidth - Gutter);
        await WriteOutputLineAsync($"{Indent(Gutter)}{line}");
    }

    private async Task WriteHeaderAsync(VcaHookDisplayInfo info)
    {
        if (_style.Enabled)
        {
            await WriteOutputLineAsync("");
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

    private static VcaHookKind ParseHookKind(IReadOnlyDictionary<string, string>? details) =>
        Enum.TryParse<VcaHookKind>(
            details != null && details.TryGetValue("hookKind", out var hookKind) ? hookKind : null,
            out var parsed)
            ? parsed
            : VcaHookKind.PreCommit;

    private static string DescribeHookKind(VcaHookKind kind) => kind switch
    {
        VcaHookKind.CommitMessage => "Commit Message",
        VcaHookKind.AcknowledgeCommitMessage => "Commit Acknowledgment",
        VcaHookKind.Preview => "Preview",
        _ => "Pre-Commit"
    };

    /// <summary>
    /// Title row plus a rule across the full line width. A drawn box was the previous look, but a
    /// box sized to the window is mostly empty rectangle, and a box sized to its text leaves a
    /// second right edge disagreeing with every row beneath it.
    /// </summary>
    private async Task WriteBannerAsync(string kind)
    {
        var rule = LineWidth - Gutter;
        var padding = Math.Max(1, rule - Brand.Length - kind.Length);
        await WriteOutputLineAsync(
            $"{Indent(Gutter)}{_style.Bold}{GradientText(Brand, BrandFrom, BrandTo)}" +
            $"{new string(' ', padding)}{_style.Bold}{_style.Magenta}{kind}{_style.Reset}");
        await WriteOutputLineAsync(
            $"{Indent(Gutter)}{_style.Dim}{new string('─', rule)}{_style.Reset}");
    }

    /// <summary>
    /// Color-hints well-known VCA transcript prefixes (PASS/WARN/STOP/ERROR) so buffered step
    /// prose reads at a glance. Plain mode returns nothing so the line stays untouched.
    /// </summary>
    private string DecorationFor(string line)
    {
        if (!_style.Enabled)
        {
            return "";
        }

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("PASS", StringComparison.Ordinal))
        {
            return _style.Green;
        }
        if (trimmed.StartsWith("[WARN]", StringComparison.Ordinal) ||
            trimmed.StartsWith("WARN", StringComparison.Ordinal))
        {
            return _style.Yellow;
        }
        if (trimmed.StartsWith("[STOP]", StringComparison.Ordinal) ||
            trimmed.StartsWith("STOP", StringComparison.Ordinal) ||
            trimmed.StartsWith("ERROR", StringComparison.Ordinal))
        {
            return _style.Red;
        }

        return "";
    }

    private string DecorateTranscriptLine(string line)
    {
        var decoration = DecorationFor(line);
        return decoration.Length == 0 ? line : $"{decoration}{line}{_style.Reset}";
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
                _options.Output.Write(_style.Enabled
                    ? $"\r{_style.ClearLine}{BuildStepLine(preflightEvent, SpinnerFrame(index++), _style.Cyan, preflightEvent.Message, "", FormatElapsed(started.Elapsed))}"
                    : $"\r{StepPrefix(preflightEvent)} {SpinnerFrame(index++)} [run] " +
                        $"{preflightEvent.Message} · {FormatElapsed(started.Elapsed)}");
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
                    $"\r{_style.ClearLine}{Indent(Gutter)}{_style.Cyan}{SpinnerFrame(index++)}{_style.Reset}  " +
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
