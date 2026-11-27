using VibeRails.Data.Sqlite;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.Jobs;
using VibeRails.DB;
using VibeRails.Utils;

namespace VibeRails.Services.VCA.Hooks;

/// <summary>
/// Standalone process host for git-hook VCA mode. This keeps Program.cs to a
/// tiny argv handoff while the hook-mode wiring remains isolated and testable.
/// </summary>
public static class VcaHookProcessHost
{
    /// <summary>
    /// How long a blocking popup stays up before closing itself. Successful/non-blocking
    /// checks close immediately; only a failure needs reading time. Shared with
    /// <see cref="VcaHookRunner"/> so blocking pauses cannot drift apart, and every user-facing
    /// "auto-closes in …" string is formatted from it rather than restated.
    /// </summary>
    internal static readonly TimeSpan ConsolePauseTimeout = TimeSpan.FromSeconds(30);

    public static bool IsRequested(string[] args) => VcaHookCommandParser.IsRequested(args);

    public static Task<int> RunAsync(
        string[] args,
        TextWriter? output = null,
        TextWriter? error = null,
        TextReader? input = null,
        CancellationToken cancellationToken = default)
        => RunCoreAsync(args, null, output, error, input, cancellationToken);

    /// <summary>Opens Git Guard's window for this run; null means none could open.</summary>
    internal delegate Task<int?> GitGuardWindow(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken);

    // Tests exercise the real host with an isolated store instead of opening the current user's
    // database, and replace the window so none opens. Production callers keep the same public entry
    // point, service registrations and CREATE_NEW_CONSOLE window.
    internal static async Task<int> RunCoreAsync(
        string[] args,
        Action<IServiceCollection>? configureServices,
        TextWriter? output = null,
        TextWriter? error = null,
        TextReader? input = null,
        CancellationToken cancellationToken = default,
        GitGuardWindow? openWindow = null)
    {
        openWindow ??= VcaHookConsoleRespawn.TryRespawnAsync;
        var usesDefaultOutput = output == null;
        output ??= Console.Out;
        error ??= Console.Error;
        input ??= Console.In;

        var invocation = new VcaHookCommandParser().Parse(args);

        if (invocation.Kind == VcaHookKind.CleanCommitMessage)
        {
            return await RunCommitMessageCleanupAsync(invocation, output, error, cancellationToken);
        }

        // When --console-window is requested and we're not already the respawned child,
        // re-launch ourselves with CREATE_NEW_CONSOLE. This produces a visible popup in
        // every Windows commit scenario (terminal, VS Code SCM panel, GUI Git client).
        // AllocConsole can't help here: it fails when the current process already has a
        // console (every terminal commit), and even when it succeeds the window
        // auto-closes in 800ms on success so users never see it.
        //
        // A commit checks first without a window and opens one only when the check fails
        // (VIBE-374): a passing commit has nothing to read or answer, and a window that
        // flashes open and shut on every commit was all a person ever saw of it.
        if (invocation.ShowConsoleWindow && !invocation.ConsoleWindowAttached)
        {
            WindowlessCheck? checkedFirst = null;
            if (OpensWindowOnlyWhenNeeded(invocation))
            {
                checkedFirst = await RunWithoutWindowAsync(invocation, configureServices, cancellationToken);
                if (checkedFirst.ExitCode == 0)
                {
                    // A clean pass says so in one line; a pass with something to read keeps all of it.
                    await output.WriteAsync(checkedFirst.HasFindings
                        ? checkedFirst.Transcript
                        : PassedLine(invocation.Kind) + Environment.NewLine);
                    await output.FlushAsync(cancellationToken);
                    return 0;
                }
            }

            var respawnResult = await openWindow(args, output, error, cancellationToken);
            if (respawnResult.HasValue)
            {
                return respawnResult.Value;
            }

            // Respawn was skipped or failed. Continue running in the current terminal,
            // but don't try to prompt for acknowledgments if there's no interactive stdin.
            if (!VcaHookProcessLaunch.CanPromptInCurrentConsole())
            {
                // Nobody can answer here, so a check already made without a window is the result.
                if (checkedFirst is { } result)
                {
                    await output.WriteAsync(result.Transcript);
                    await output.FlushAsync(cancellationToken);
                    return result.ExitCode;
                }

                invocation = invocation with { PromptForAcknowledgment = false };
            }
        }

        if (invocation.ConsoleWindowAttached && OperatingSystem.IsWindows())
        {
            try
            {
                Console.Title = $"VibeRails VCA - {FormatTitle(invocation.Kind)}";
            }
            catch
            {
                // Console title is best-effort; ignore failures (e.g. no console attached).
            }
        }

        var enableSpinner = invocation.ConsoleWindowAttached ||
            (usesDefaultOutput && !Console.IsOutputRedirected);

        // Styled (ANSI + UTF-8) output is only ever sent to a real console we prepared
        // ourselves; redirected transcripts (tests, the Rules page validator, VS Code's
        // SCM log) always get the historical plain text.
        var style = usesDefaultOutput && enableSpinner && VcaConsoleStyle.TryEnableForCurrentConsole()
            ? VcaConsoleStyle.Ansi
            : VcaConsoleStyle.Plain;

        if (style.Enabled)
        {
            // Switching Console.OutputEncoding to UTF-8 recreates Console.Out/Error;
            // the writers captured above still encode with the original OEM codepage,
            // which the now-UTF-8 console would render as mojibake. Re-capture them.
            output = Console.Out;
            error = Console.Error;
        }

        // The signing popup needs no automation store and no preflight pipeline: it signs the tree
        // the commit-msg hook already validated, and that hook re-validates once this window closes.
        if (invocation.Kind == VcaHookKind.SignCommitMessage)
        {
            var signExitCode = await HumanSignaturePopup.RunInConsoleAsync(invocation, output, style, cancellationToken);
            if (invocation.ConsoleWindowAttached && signExitCode != 0)
            {
                await PauseForEnterAsync(output, input, signExitCode, ConsolePauseTimeout, cancellationToken, style);
            }
            return signExitCode;
        }

        // The review approval popup (VB-50 slice D) is the same window family: no store, no preflight.
        // Nothing waits on this window the way a commit does, so it pauses on success too, long enough
        // to read that the approval was recorded.
        if (invocation.Kind == VcaHookKind.SignReview)
        {
            var reviewExitCode = await ReviewSignaturePopup.RunInConsoleAsync(invocation, output, style, cancellationToken);
            if (invocation.ConsoleWindowAttached)
            {
                await PauseForEnterAsync(output, input, reviewExitCode, ConsolePauseTimeout, cancellationToken, style);
            }
            return reviewExitCode;
        }

        if (invocation.Kind == VcaHookKind.SignStopBypass)
        {
            var result = await StopBypassPopup.RunInConsoleAsync(invocation, output, cancellationToken);
            if (invocation.ConsoleWindowAttached)
                await PauseForEnterAsync(output, input, result, ConsolePauseTimeout, cancellationToken, style);
            return result;
        }

        return await RunValidationAsync(
            invocation,
            configureServices,
            output,
            error,
            input,
            enableSpinner,
            style,
            cancellationToken);
    }

    /// <summary>
    /// Whether this run is a commit's pre-commit or commit-msg check, which opens Git Guard's window
    /// only when the check fails (VIBE-374). Only the installed hook scripts pass
    /// <c>--enqueue-automations</c>; the Rules page's "open the Git Guard console" button does not,
    /// so the window it asks for still opens on a pass.
    /// </summary>
    internal static bool OpensWindowOnlyWhenNeeded(VcaHookInvocation invocation) =>
        invocation.Kind is VcaHookKind.PreCommit or VcaHookKind.CommitMessage
        && invocation.EnqueueAutomatedJobs
        && !invocation.DemoUi
        && !invocation.WorkingTreeScope;

    /// <summary>What a clean windowless pass hands Git instead of its transcript.</summary>
    internal static string PassedLine(VcaHookKind kind) =>
        $"VibeRails Git Guard: {(kind == VcaHookKind.CommitMessage ? "commit message" : "pre-commit")} checks passed.";

    /// <summary>A windowless check: its exit code, its transcript and whether anything in it needs reading.</summary>
    private sealed record WindowlessCheck(int ExitCode, string Transcript, bool HasFindings);

    /// <summary>
    /// The commit's check with no window and nobody to ask: nothing is prompted, and the transcript is
    /// kept so a pass with something to read can hand it to Git (a terminal shows it, a Git GUI keeps it
    /// in its log). A failure is checked again in the window, where a person reads it and can answer it,
    /// so this transcript is dropped then: it would say no window could open.
    ///
    /// Running the check twice on a failure is safe. Rule responses are queued once per staged snapshot
    /// (their occurrence ignores the hook run), before-commit Automations start only on a pass, and
    /// message cleanup, stored approvals and STOP evidence change the message only when it lacks them.
    /// The second run also reuses the first one's staged capture (StagedSnapshotShare).
    /// </summary>
    private static async Task<WindowlessCheck> RunWithoutWindowAsync(
        VcaHookInvocation invocation,
        Action<IServiceCollection>? configureServices,
        CancellationToken cancellationToken)
    {
        await using var buffer = new StringWriter();
        var transcript = TextWriter.Synchronized(buffer);
        FindingsWatch? watch = null;
        var exitCode = await RunValidationAsync(
            invocation with { PromptForAcknowledgment = false, ShowConsoleWindow = false },
            configureServices,
            transcript,
            transcript,
            TextReader.Null,
            enableSpinner: false,
            VcaConsoleStyle.Plain,
            cancellationToken,
            services => services.AddSingleton<IVcaHookPresenter>(_ => watch = new FindingsWatch(
                new VcaConsoleHookPresenter(new VcaHookConsoleOptions(
                    transcript, transcript, TextReader.Null, EnableSpinner: false, VcaConsoleStyle.Plain)))));
        await transcript.FlushAsync(cancellationToken);
        return new WindowlessCheck(exitCode, buffer.ToString(), watch?.HasFindings ?? true);
    }

    private static async Task<int> RunValidationAsync(
        VcaHookInvocation invocation,
        Action<IServiceCollection>? configureServices,
        TextWriter output,
        TextWriter error,
        TextReader input,
        bool enableSpinner,
        VcaConsoleStyle style,
        CancellationToken cancellationToken,
        Action<IServiceCollection>? configureHost = null)
    {
        var services = new ServiceCollection();
        ConfigureServices(
            services,
            output,
            error,
            input,
            enableSpinner,
            style);
        configureServices?.Invoke(services);
        configureHost?.Invoke(services);

        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IVcaHookRunner>();

        var exitCode = await runner.RunAsync(invocation, cancellationToken);

        // A successful/non-blocking popup closes as soon as its transcript is complete, so
        // Git never waits for an unnecessary Enter key. Blocking results remain visible long
        // enough to read, with Enter/timeout escape hatches. The parent never reaches here.
        // A person who just closed the STOP exception offer has already had that pause.
        if (invocation.ConsoleWindowAttached && exitCode != 0
            && provider.GetService<IStopBypassPrompt>()?.Declined != true)
        {
            await PauseForEnterAsync(
                output,
                input,
                exitCode,
                ConsolePauseTimeout,
                cancellationToken,
                style);
        }

        return exitCode;
    }

    /// <summary>
    /// Commit-message cleanup on its own, invoked by the hook script before it runs any chained
    /// commit-msg hook.
    ///
    /// Order is the whole point. Cleanup rewrites the message file, so a chained hook that
    /// validates, signs, or derives metadata from the message has to see the text Git will record —
    /// running cleanup afterwards showed that hook one message and committed another.
    ///
    /// Builds no service graph and produces no UI: it sits on the critical path of every commit and
    /// has nothing to validate. It also never fails the commit. A policy whose job is to delete
    /// trailers has no business rejecting a message it could not read, and the validation pass that
    /// follows is what decides whether the commit proceeds.
    /// </summary>
    private static async Task<int> RunCommitMessageCleanupAsync(
        VcaHookInvocation invocation,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        try
        {
            var removed = await new CommitMessageCoAuthorCleaner()
                .RemoveAsync(invocation.CommitMessagePath, cancellationToken);
            if (removed > 0)
            {
                var suffix = removed == 1 ? "trailer" : "trailers";
                await output.WriteLineAsync(
                    $"VibeRails removed {removed} {suffix} from the commit message (Co-authored-by / Claude-Session cleanup).");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Commit-message trailer cleanup failed");
            await error.WriteLineAsync(
                "VibeRails could not apply commit-message cleanup; the message was left unchanged.");
        }

        return 0;
    }

    internal static async Task PauseForEnterAsync(
        TextWriter output,
        TextReader input,
        int exitCode,
        TimeSpan timeout,
        CancellationToken cancellationToken = default,
        VcaConsoleStyle? style = null)
    {
        style ??= VcaConsoleStyle.Plain;
        try
        {
            await output.WriteLineAsync();

            Task<string?> readTask;
            bool closedByUser;
            if (style.Enabled)
            {
                readTask = ReadLineInBackground(input);
                closedByUser = await CountdownForEnterAsync(
                    output, readTask, exitCode, timeout, style, cancellationToken);
                await output.WriteLineAsync();
            }
            else
            {
                var autoClose = DescribeAutoClose(timeout);
                var message = exitCode == 0
                    ? $"VCA check complete. Press Enter to close this window ({autoClose})..."
                    : $"VCA check blocked the commit (exit code {exitCode}). "
                        + $"Press Enter to close this window ({autoClose})...";
                await output.WriteAsync(message);
                await output.FlushAsync();

                readTask = ReadLineInBackground(input);
                closedByUser = await Task.WhenAny(
                    readTask,
                    Task.Delay(timeout, cancellationToken)) == readTask;
            }

            if (closedByUser)
            {
                await readTask;
            }
            else
            {
                // The console is about to close, which will end the outstanding read.
                // Observe a resulting fault so it cannot become unobserved.
                _ = readTask.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
        }
        catch
        {
            // Best effort — if stdin is closed or unavailable, just exit.
        }
    }

    /// <summary>
    /// Reads one line on a thread-pool thread, so a caller can race it against a timeout.
    /// <c>Console.In</c> is a <c>SyncTextReader</c>, whose <c>ReadLineAsync</c> is
    /// <c>Task.FromResult(ReadLine())</c>: it blocks the calling thread until Enter. Awaited
    /// directly, it made every "auto-closes in 30 seconds" popup wait for Enter forever, which is
    /// how a stale "no signing key" window outlived the successful signing after it (VIBE-200).
    /// A read still pending when the timeout wins is abandoned on its background thread; the
    /// console ends it when the process exits.
    /// </summary>
    internal static Task<string?> ReadLineInBackground(TextReader input) =>
        Task.Run(() => input.ReadLineAsync());

    /// <summary>
    /// Styled pause line with a live once-per-second countdown, so the popup visibly
    /// ticks toward auto-close instead of sitting on the timeout of static text.
    /// Returns true when the user pressed Enter, false when the timeout elapsed.
    /// </summary>
    private static async Task<bool> CountdownForEnterAsync(
        TextWriter output,
        Task<string?> readTask,
        int exitCode,
        TimeSpan timeout,
        VcaConsoleStyle style,
        CancellationToken cancellationToken)
    {
        var verdict = exitCode == 0
            ? $"{style.Green}✓ VCA check complete.{style.Reset}"
            : $"{style.Red}✕ VCA check blocked the commit (exit code {exitCode}).{style.Reset}";
        var tickInterval = TimeSpan.FromSeconds(1);
        var clock = Stopwatch.StartNew();

        while (true)
        {
            var remaining = timeout - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await output.WriteAsync(
                $"\r{style.ClearLine}{verdict}  " +
                $"{CountdownBar(remaining, timeout, style)} " +
                $"{style.Dim}auto-closes in{style.Reset} {style.Bold}{FormatCountdown(remaining)}{style.Reset}  " +
                $"{style.Dim}Press{style.Reset} {style.Bold}Enter{style.Reset} " +
                $"{style.Dim}to close now{style.Reset} ");
            await output.FlushAsync();

            var tick = Task.Delay(remaining < tickInterval ? remaining : tickInterval, cancellationToken);
            if (await Task.WhenAny(readTask, tick) == readTask)
            {
                return true;
            }
        }
    }

    /// <summary>
    /// Renders a pause timeout for the unstyled prompt, so the wording tracks
    /// <see cref="ConsolePauseTimeout"/> instead of being restated as a literal that can go stale.
    /// </summary>
    internal static string DescribeAutoClose(TimeSpan timeout)
    {
        var seconds = (int)Math.Ceiling(timeout.TotalSeconds);
        if (seconds < 60)
            return $"auto-closes in {seconds} second{(seconds == 1 ? string.Empty : "s")}";

        var minutes = timeout.TotalMinutes;
        return $"auto-closes in {minutes:0.#} minute{(minutes == 1 ? string.Empty : "s")}";
    }

    private static string FormatCountdown(TimeSpan remaining)
    {
        var rounded = TimeSpan.FromSeconds(Math.Ceiling(remaining.TotalSeconds));
        return $"{(int)rounded.TotalMinutes}:{rounded.Seconds:00}";
    }

    /// <summary>
    /// Draining time bar for the auto-close countdown; shifts green → yellow → red
    /// as the deadline approaches.
    /// </summary>
    private static string CountdownBar(TimeSpan remaining, TimeSpan timeout, VcaConsoleStyle style)
    {
        const int cells = 20;
        var fraction = timeout > TimeSpan.Zero
            ? Math.Clamp(remaining.TotalMilliseconds / timeout.TotalMilliseconds, 0, 1)
            : 0;
        var filled = (int)Math.Ceiling(fraction * cells);
        var color = fraction > 0.5 ? style.Green : fraction > 0.25 ? style.Yellow : style.Red;
        return $"{style.Dim}[{style.Reset}" +
            $"{color}{new string('█', filled)}{style.Reset}" +
            $"{style.Dim}{new string('░', cells - filled)}]{style.Reset}";
    }

    private static string FormatTitle(VcaHookKind kind) => kind switch
    {
        VcaHookKind.CommitMessage => "Commit Message",
        VcaHookKind.AcknowledgeCommitMessage => "Commit Acknowledgment",
        VcaHookKind.SignCommitMessage => "Human Verification",
        VcaHookKind.SignReview => "Review Approval",
        VcaHookKind.SignStopBypass => "STOP Bypass Approval",
        VcaHookKind.Preview => "Preview",
        _ => "Pre-Commit"
    };

    internal static void ConfigureServices(
        IServiceCollection services,
        TextWriter output,
        TextWriter error,
        TextReader input,
        bool enableSpinner,
        VcaConsoleStyle? style = null)
    {
        services.AddSingleton<IVcaHookCommandParser, VcaHookCommandParser>();
        services.AddSingleton<ICommitMessageCoAuthorCleaner, CommitMessageCoAuthorCleaner>();
        services.AddSingleton<IVcaHookRunner, VcaHookRunner>();
        services.AddSingleton<IVcaHookValidationAnalyzer, VcaHookValidationAnalyzer>();
        services.AddSqliteJobStorage(_ => StatePath());
        // Resolved lazily by quality exemptions; an unreadable Board must not block local Jobs.
        services.AddSqliteBoardStorage(_ => StatePath());
        // Approvals a reviewer already gave over the staged tree (VB-50). Same file; read only here,
        // and resolved only when a signature finding is pending, so building the runner opens nothing.
        services.AddSqliteReviewRequestStorage(_ => StatePath());
        services.AddSingleton<IStoredCommitApprovalSource>(sp =>
            new ReviewRequestApprovalSource(() => sp.GetService<VibeRails.DB.IReviewRequestStore>()));
        services.AddGitPreflight();
        services.AddSingleton<IVcaHookPresenter>(_ =>
            new VcaConsoleHookPresenter(new VcaHookConsoleOptions(output, error, input, enableSpinner, style)));
        services.AddSingleton<IHumanSignaturePrompt>(_ => new HumanSignaturePromptLauncher(output, error, style));
        services.AddSingleton<IStopBypassPrompt>(sp =>
            new StopBypassHookPrompt(output, error, sp.GetRequiredService<IGitStagedSnapshotProvider>(), style));
    }

    private static string StatePath()
    {
        var installDirectory = PathConstants.GetInstallDirPath();
        Directory.CreateDirectory(installDirectory);
        return Path.Combine(installDirectory, PathConstants.STATE_FILENAME);
    }

    /// <summary>
    /// Passes the transcript through and notes whether a one-line pass would hide something a person
    /// should read: a warning, failure or error line (trailer cleanup, a missing popup), a step that
    /// ended neither passed nor skipped (WARN findings, COMMIT acknowledgments, rule responses), or
    /// before-commit Automations the check queued.
    /// </summary>
    private sealed class FindingsWatch(IVcaHookPresenter inner) : IVcaHookPresenter
    {
        private volatile bool _hasFindings;

        public bool HasFindings => _hasFindings;

        public Task WritePreflightEventAsync(GitPreflightEvent preflightEvent, CancellationToken cancellationToken)
        {
            if (preflightEvent.Type == GitPreflightEventType.StepFinished
                && (preflightEvent.Status is not (GitPreflightStepStatus.Passed or GitPreflightStepStatus.Skipped)
                    || (preflightEvent.StepId == AutomatedWorkflowsPreflightStep.Id
                        && preflightEvent.Status == GitPreflightStepStatus.Passed)))
            {
                _hasFindings = true;
            }

            return inner.WritePreflightEventAsync(preflightEvent, cancellationToken);
        }

        public Task<T> RunWithProgressAsync<T>(
            VcaHookDisplayInfo displayInfo,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken) =>
            inner.RunWithProgressAsync(displayInfo, operation, cancellationToken);

        public Task WriteValidationOutputAsync(string validationOutput) => inner.WriteValidationOutputAsync(validationOutput);

        public Task WriteSuccessAsync(string message) => inner.WriteSuccessAsync(message);

        public Task WriteWarningAsync(string message)
        {
            _hasFindings = true;
            return inner.WriteWarningAsync(message);
        }

        public Task WriteFailureAsync(string message)
        {
            _hasFindings = true;
            return inner.WriteFailureAsync(message);
        }

        public Task WriteErrorAsync(string message)
        {
            _hasFindings = true;
            return inner.WriteErrorAsync(message);
        }

        public Task<string?> ReadLineAsync(string prompt) => inner.ReadLineAsync(prompt);
    }
}
