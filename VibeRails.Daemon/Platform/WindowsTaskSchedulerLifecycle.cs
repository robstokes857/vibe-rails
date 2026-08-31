namespace VibeRails.Daemon.Platform;

internal sealed class WindowsTaskSchedulerLifecycle(
    ScopedDaemonRegistration scoped,
    IDaemonProcessRunner processRunner,
    Ipc.IDaemonControlClient controlClient)
    : DaemonPlatformLifecycleBase(scoped, processRunner, controlClient)
{
    public override DaemonPlatformKind Platform => DaemonPlatformKind.Windows;

    internal TimeSpan GracefulShutdownTimeout { get; set; } = ControlTimeout;

    public override async Task<DaemonRegistrationInspection> InspectAsync(CancellationToken cancellationToken)
    {
        var query = await RunAsync(
            SchtasksPath(),
            ["/Query", "/TN", Scoped.WindowsTaskName, "/XML"],
            cancellationToken,
            requireSuccess: false).ConfigureAwait(false);
        if (!query.Succeeded)
            return new DaemonRegistrationInspection(DaemonRegistrationCondition.NotInstalled);

        return DaemonServiceDefinitionRenderer.IsCurrentWindowsTaskXml(query.StandardOutput, Scoped)
            ? new DaemonRegistrationInspection(DaemonRegistrationCondition.Current)
            : new DaemonRegistrationInspection(
                DaemonRegistrationCondition.Stale,
                "The Windows Task Scheduler registration does not match this VibeRails build.");
    }

    public override async Task InstallAsync(CancellationToken cancellationToken)
    {
        await WriteTaskAsync(cancellationToken).ConfigureAwait(false);
        await StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public override Task StartAsync(CancellationToken cancellationToken) => RunRequiredAsync(
        ["/Run", "/TN", Scoped.WindowsTaskName], cancellationToken);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        var shutdown = await RequestGracefulShutdownAsync(cancellationToken).ConfigureAwait(false);
        if (shutdown.Outcome == Ipc.DaemonControlClientOutcome.Success &&
            await WaitForInstanceGuardReleaseAsync(GracefulShutdownTimeout, cancellationToken)
                .ConfigureAwait(false))
        {
            return;
        }

        await RunAsync(
            SchtasksPath(),
            ["/End", "/TN", Scoped.WindowsTaskName],
            cancellationToken,
            requireSuccess: false).ConfigureAwait(false);
    }

    public override Task RepairRegistrationAsync(CancellationToken cancellationToken) =>
        WriteTaskAsync(cancellationToken);

    public override async Task UninstallAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        await RunAsync(
            SchtasksPath(),
            ["/Delete", "/TN", Scoped.WindowsTaskName, "/F"],
            cancellationToken,
            requireSuccess: false).ConfigureAwait(false);
    }

    public override async Task CleanupLegacyRegistrationsAsync(CancellationToken cancellationToken)
    {
        foreach (var taskName in Scoped.Registration.LegacyRegistrations.WindowsTaskNames)
        {
            var query = await RunAsync(
                SchtasksPath(),
                ["/Query", "/TN", taskName],
                cancellationToken,
                requireSuccess: false).ConfigureAwait(false);
            if (!query.Succeeded)
                continue;

            await RunAsync(
                SchtasksPath(),
                ["/End", "/TN", taskName],
                cancellationToken,
                requireSuccess: false).ConfigureAwait(false);
            await RunAsync(
                SchtasksPath(),
                ["/Delete", "/TN", taskName, "/F"],
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteTaskAsync(CancellationToken cancellationToken)
    {
        EnsurePrivateDirectory(Scoped.Registration.DataDirectory);
        var temporaryPath = Path.Combine(
            Scoped.Registration.DataDirectory,
            $".{Scoped.Registration.ApplicationId}-{Guid.NewGuid():N}.task.xml");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                DaemonServiceDefinitionRenderer.RenderWindowsTaskXml(Scoped),
                new System.Text.UTF8Encoding(false),
                cancellationToken).ConfigureAwait(false);
            await RunAsync(
                SchtasksPath(),
                ["/Create", "/TN", Scoped.WindowsTaskName, "/XML", temporaryPath, "/F"],
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // The task is already registered; an orphaned private temp file is recoverable.
            }
        }
    }

    private async Task RunRequiredAsync(string[] arguments, CancellationToken cancellationToken) =>
        _ = await RunAsync(SchtasksPath(), arguments, cancellationToken).ConfigureAwait(false);

    private static string SchtasksPath()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return string.IsNullOrWhiteSpace(system)
            ? "schtasks.exe"
            : Path.Combine(system, "schtasks.exe");
    }
}
