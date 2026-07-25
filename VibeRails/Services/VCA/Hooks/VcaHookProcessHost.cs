using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using VibeRails.Services.GitPreflight;
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
    /// How long the popup stays up before closing itself. The window exists so the user can read
    /// the outcome — most importantly a list of STOP violations — so this has to be long enough to
    /// actually read that, while still short enough that an abandoned window cannot hold Git open
    /// indefinitely. Shared with <see cref="VcaHookRunner"/> so the two pauses cannot drift apart,
    /// and every user-facing "auto-closes in …" string is formatted from it rather than restated.
    /// </summary>
    internal static readonly TimeSpan ConsolePauseTimeout = TimeSpan.FromSeconds(30);

    public static bool IsRequested(string[] args) => VcaHookCommandParser.IsRequested(args);

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter? output = null,
        TextWriter? error = null,
        TextReader? input = null,
        CancellationToken cancellationToken = default)
    {
        var usesDefaultOutput = output == null;
        output ??= Console.Out;
        error ??= Console.Error;
        input ??= Console.In;

        var invocation = new VcaHookCommandParser().Parse(args);

        // When --console-window is requested and we're not already the respawned child,
        // re-launch ourselves with CREATE_NEW_CONSOLE. This produces a visible popup in
        // every Windows commit scenario (terminal, VS Code SCM panel, GUI Git client).
        // AllocConsole can't help here: it fails when the current process already has a
        // console (every terminal commit), and even when it succeeds the window
        // auto-closes in 800ms on success so users never see it.
        if (invocation.ShowConsoleWindow && !invocation.ConsoleWindowAttached)
        {
            var respawnResult = await VcaHookConsoleRespawn.TryRespawnAsync(
                args, output, error, cancellationToken);
            if (respawnResult.HasValue)
            {
                return respawnResult.Value;
            }

            // Respawn was skipped or failed. Continue running in the current terminal,
            // but don't try to prompt for acknowledgments if there's no interactive stdin.
            if (!VcaHookProcessLaunch.CanPromptInCurrentConsole())
            {
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

        var services = new ServiceCollection();
        ConfigureServices(
            services,
            output,
            error,
            input,
            enableSpinner,
            style);

        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IVcaHookRunner>();

        var exitCode = await runner.RunAsync(invocation, cancellationToken);

        // The respawned child pauses so the user can read the result before the popup
        // disappears, but auto-closes so an abandoned window cannot hold Git forever.
        // The parent returns earlier and never reaches this branch.
        if (invocation.ConsoleWindowAttached)
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
                readTask = input.ReadLineAsync();
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

                readTask = input.ReadLineAsync();
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
        services.AddSingleton<IVcaHookRunner, VcaHookRunner>();
        services.AddSingleton<IVcaHookValidationAnalyzer, VcaHookValidationAnalyzer>();
        services.AddSingleton<IJobStore>(_ =>
        {
            var installDirectory = PathConstants.GetInstallDirPath();
            Directory.CreateDirectory(installDirectory);
            var statePath = Path.Combine(installDirectory, PathConstants.STATE_FILENAME);
            return new JobStore($"Data Source={statePath};Mode=ReadWriteCreate;Cache=Shared");
        });
        services.AddGitPreflight();
        services.AddSingleton<IVcaHookPresenter>(_ =>
            new VcaConsoleHookPresenter(new VcaHookConsoleOptions(output, error, input, enableSpinner, style)));
    }
}
