using Microsoft.Extensions.DependencyInjection;

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
        CancellationToken cancellationToken = default)
    {
        output ??= Console.Out;
        error ??= Console.Error;

        var services = new ServiceCollection();
        ConfigureServices(services, output, error, enableSpinner: !Console.IsOutputRedirected);

        await using var provider = services.BuildServiceProvider();
        var parser = provider.GetRequiredService<IVcaHookCommandParser>();
        var runner = provider.GetRequiredService<IVcaHookRunner>();

        var invocation = parser.Parse(args);
        return await runner.RunAsync(invocation, cancellationToken);
    }

    internal static void ConfigureServices(
        IServiceCollection services,
        TextWriter output,
        TextWriter error,
        bool enableSpinner)
    {
        services.AddSingleton<IVcaHookCommandParser, VcaHookCommandParser>();
        services.AddSingleton<IVcaHookRunner, VcaHookRunner>();
        services.AddSingleton<IVcaHookValidationService, VcaRulesHookValidationService>();
        services.AddSingleton<IVcaHookValidationAnalyzer, VcaHookValidationAnalyzer>();
        services.AddSingleton<IVcaHookFileProvider, VcaHookFileProvider>();
        services.AddSingleton<IVcaHookPresenter>(_ =>
            new VcaConsoleHookPresenter(new VcaHookConsoleOptions(output, error, enableSpinner)));
    }
}
