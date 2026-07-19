using Microsoft.Extensions.DependencyInjection;
using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.VCA.Hooks;

/// <summary>
/// Standalone process host for git-hook VCA mode. This keeps Program.cs to a
/// tiny argv handoff while the hook-mode wiring remains isolated and testable.
/// </summary>
public static class VcaHookProcessHost
{
    private static readonly TimeSpan ConsolePauseTimeout = TimeSpan.FromMinutes(2);

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

        var services = new ServiceCollection();
        ConfigureServices(
            services,
            output,
            error,
            input,
            enableSpinner: invocation.ConsoleWindowAttached ||
                (usesDefaultOutput && !Console.IsOutputRedirected));

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
                cancellationToken);
        }

        return exitCode;
    }

    internal static async Task PauseForEnterAsync(
        TextWriter output,
        TextReader input,
        int exitCode,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var message = exitCode == 0
                ? "VCA check complete. Press Enter to close this window (auto-closes in 2 minutes)..."
                : $"VCA check blocked the commit (exit code {exitCode}). "
                    + "Press Enter to close this window (auto-closes in 2 minutes)...";
            await output.WriteLineAsync();
            await output.WriteAsync(message);
            await output.FlushAsync();

            var readTask = input.ReadLineAsync();
            var completed = await Task.WhenAny(
                readTask,
                Task.Delay(timeout, cancellationToken));
            if (completed == readTask)
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
        bool enableSpinner)
    {
        services.AddSingleton<IVcaHookCommandParser, VcaHookCommandParser>();
        services.AddSingleton<IVcaHookRunner, VcaHookRunner>();
        services.AddSingleton<IVcaHookValidationAnalyzer, VcaHookValidationAnalyzer>();
        services.AddGitPreflight();
        services.AddSingleton<IVcaHookPresenter>(_ =>
            new VcaConsoleHookPresenter(new VcaHookConsoleOptions(output, error, input, enableSpinner)));
    }
}
