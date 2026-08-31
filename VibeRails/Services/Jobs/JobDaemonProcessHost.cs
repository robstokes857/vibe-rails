using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using VibeRails.Daemon;
using VibeRails.Daemon.Ipc;
using VibeRails.DB;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

/// <summary>
/// Lean process role for VibeRails Demon. It hosts only the durable Automation scheduler and its
/// native-terminal launch graph; it never constructs Kestrel or the dashboard service graph.
/// </summary>
public static class JobDaemonProcessHost
{
    public const string Argument = "--job-daemon";

    public static bool IsRequested(IReadOnlyList<string> args)
    {
        foreach (var argument in args)
        {
            if (argument == "--")
                return false;
            if (argument.Equals(Argument, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        CancellationToken cancellationToken = default)
    {
        ParserConfigs.ParseArgs(args.ToArray());

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = [],
            ApplicationName = typeof(JobDaemonProcessHost).Assembly.GetName().Name,
            ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.Configuration.AddJsonFile(
            Path.Combine(AppContext.BaseDirectory, "appsettings.json"),
            optional: true,
            reloadOnChange: false);

        var installDirectoryName = builder.Configuration["VibeRails:InstallDirName"]
            ?? PathConstants.DEFAULT_INSTALL_DIR_NAME;
        GlobalRuntimePaths.Initialize(installDirectoryName);

        var identityProvider = new CurrentUserIdentityProvider();
        // Retry over a short window: dashboard polls and the installer probe liveness by briefly
        // acquiring this same guard. A single attempt could lose that microsecond race, exit 0,
        // and leave the daemon silently down until the next logon trigger.
        using var instanceGuard = await DaemonInstanceGuard.TryAcquireWithRetryAsync(
            JobDaemonRegistrationFactory.ApplicationId,
            identityProvider.GetCurrent(),
            PathConstants.GetInstallDirPath(),
            retryWindow: TimeSpan.FromSeconds(3),
            cancellationToken);
        if (instanceGuard is null)
        {
            Log.Information("[VBD] Another VibeRails Demon instance already owns the current-user guard");
            return 0;
        }

        builder.Services.AddAutomationRuntime(hostScheduler: true);
        builder.Services.AddSingleton<ICurrentUserIdentityProvider>(identityProvider);
        builder.Services.AddSingleton(JobDaemonRuntimeInfo.Create());
        builder.Services.AddSingleton<IDaemonControlHandler, JobDaemonControlHandler>();
        builder.Services.AddHostedService<JobDaemonControlHostedService>();

        using var host = builder.Build();

        // Both stores lazily initialize their own schema. Resolve them only after the shared
        // ParserConfigs paths above are populated and before hosted services can start.
        _ = host.Services.GetRequiredService<IJobStore>();
        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<IRepository>().InitializeDatabase();
        }
        PrivateFilePermissions.EnsureFile(ParserConfigs.GetStatePath());

        Log.Information(
            "[VBD] Starting lean Automation host. version={Version} processId={ProcessId} statePath={StatePath}",
            VersionInfo.Version,
            Environment.ProcessId,
            ParserConfigs.GetStatePath());

        try
        {
            await host.RunAsync(cancellationToken);
            Log.Information("[VBD] Lean Automation host stopped gracefully. processId={ProcessId}", Environment.ProcessId);
            return 0;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Information("[VBD] Lean Automation host cancelled. processId={ProcessId}", Environment.ProcessId);
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[VBD] Lean Automation host failed. processId={ProcessId}", Environment.ProcessId);
            return 1;
        }
    }
}
