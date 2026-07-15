using Microsoft.Extensions.DependencyInjection;
using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.VCA.Hooks;

/// <summary>
/// Standalone process host for git-hook VCA mode. This keeps Program.cs to a
/// tiny argv handoff while the hook-mode wiring remains isolated and testable.
/// </summary>
public static class VcaHookProcessHost
{
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
        using var consoleWindow = VcaHookConsoleWindow.TryOpen(invocation, output, error);
        if (consoleWindow != null)
        {
            output = consoleWindow.Output;
            error = consoleWindow.Error;
            input = consoleWindow.Input;
        }
        else if (invocation.ShowConsoleWindow)
        {
            // The request came from a non-terminal Git launch, but opening a console
            // was intentionally skipped (CI/service) or failed. Never wait on stdin.
            invocation = invocation with { PromptForAcknowledgment = false };
        }

        var services = new ServiceCollection();
        ConfigureServices(
            services,
            output,
            error,
            input,
            enableSpinner: consoleWindow != null || (usesDefaultOutput && !Console.IsOutputRedirected));

        await using var provider = services.BuildServiceProvider();
        var runner = provider.GetRequiredService<IVcaHookRunner>();

        var exitCode = await runner.RunAsync(invocation, cancellationToken);
        if (consoleWindow != null)
        {
            await consoleWindow.CompleteAsync(
                exitCode,
                pauseOnFailure: invocation.Kind == VcaHookKind.PreCommit);
        }

        return exitCode;
    }

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
