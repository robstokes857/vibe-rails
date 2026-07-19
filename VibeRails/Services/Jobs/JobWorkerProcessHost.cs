using System.Runtime.InteropServices;
using Serilog;
using VibeRails.DB;
using VibeRails.Utils;

namespace VibeRails.Services.Jobs;

public static class JobWorkerProcessHost
{
    public static bool IsRequested(IEnumerable<string> args) =>
        args.Any(argument => argument.Equals("--jobs-worker", StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(CancellationToken cancellationToken = default)
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        using var terminateHandler = OperatingSystem.IsWindows()
            ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                shutdown.Cancel();
            });

        try
        {
            var installDirectory = PathConstants.GetInstallDirPath();
            Directory.CreateDirectory(installDirectory);
            Directory.CreateDirectory(Path.Combine(installDirectory, PathConstants.ENVS_SUBDIR));
            Directory.CreateDirectory(Path.Combine(installDirectory, PathConstants.JOB_WORKSPACES_SUBDIR));
            var statePath = Path.Combine(installDirectory, PathConstants.STATE_FILENAME);
            ParserConfigs.SetStatePath(statePath);
            ParserConfigs.SetEnvPath(Path.Combine(installDirectory, PathConstants.ENVS_SUBDIR));

            var store = new JobStore($"Data Source={statePath};Mode=ReadWriteCreate;Cache=Shared");
            var resolver = new JobExecutableResolver();
            var executor = new JobRunExecutor(store, resolver, new JobWorkspaceService());
            return await new JobWorker(store, executor).RunAsync(shutdown.Token);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "[Jobs] Worker process failed");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }
}
