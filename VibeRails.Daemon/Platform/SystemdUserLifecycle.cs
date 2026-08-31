namespace VibeRails.Daemon.Platform;

internal sealed class SystemdUserLifecycle(
    ScopedDaemonRegistration scoped,
    IDaemonProcessRunner processRunner,
    Ipc.IDaemonControlClient controlClient)
    : DaemonPlatformLifecycleBase(scoped, processRunner, controlClient)
{
    public override DaemonPlatformKind Platform => DaemonPlatformKind.Linux;

    private string UnitDirectory => Path.Combine(
        Scoped.Identity.UserProfileDirectory,
        ".config",
        "systemd",
        "user");

    private string UnitPath => Path.Combine(UnitDirectory, Scoped.SystemdUnitName);

    public override async Task<DaemonRegistrationInspection> InspectAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(UnitPath))
            return new DaemonRegistrationInspection(DaemonRegistrationCondition.NotInstalled);

        var content = await File.ReadAllTextAsync(UnitPath, cancellationToken).ConfigureAwait(false);
        return DaemonServiceDefinitionRenderer.IsCurrentSystemdUserUnit(content, Scoped)
            ? new DaemonRegistrationInspection(DaemonRegistrationCondition.Current)
            : new DaemonRegistrationInspection(
                DaemonRegistrationCondition.Stale,
                "The systemd user unit does not match this VibeRails build.");
    }

    public override async Task InstallAsync(CancellationToken cancellationToken)
    {
        await WriteUnitAsync(cancellationToken).ConfigureAwait(false);
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        await RunSystemctlAsync(["enable", "--now", Scoped.SystemdUnitName], cancellationToken)
            .ConfigureAwait(false);
    }

    public override Task StartAsync(CancellationToken cancellationToken) =>
        RunSystemctlAsync(["start", Scoped.SystemdUnitName], cancellationToken);

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = await RequestGracefulShutdownAsync(cancellationToken).ConfigureAwait(false);
        await RunSystemctlAsync(
            ["stop", Scoped.SystemdUnitName],
            cancellationToken,
            requireSuccess: false).ConfigureAwait(false);
    }

    public override async Task RepairRegistrationAsync(CancellationToken cancellationToken)
    {
        await WriteUnitAsync(cancellationToken).ConfigureAwait(false);
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        await RunSystemctlAsync(["enable", Scoped.SystemdUnitName], cancellationToken)
            .ConfigureAwait(false);
    }

    public override async Task UninstallAsync(CancellationToken cancellationToken)
    {
        await RunSystemctlAsync(
            ["disable", "--now", Scoped.SystemdUnitName],
            cancellationToken,
            requireSuccess: false).ConfigureAwait(false);
        if (File.Exists(UnitPath))
            File.Delete(UnitPath);
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task CleanupLegacyRegistrationsAsync(CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var unitName in Scoped.Registration.LegacyRegistrations.SystemdUnitNames)
        {
            await RunSystemctlAsync(
                ["disable", "--now", unitName],
                cancellationToken,
                requireSuccess: false).ConfigureAwait(false);
            var path = Path.Combine(UnitDirectory, unitName);
            if (File.Exists(path))
            {
                File.Delete(path);
                changed = true;
            }
        }

        if (changed)
            await ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task WriteUnitAsync(CancellationToken cancellationToken) => WritePrivateFileAsync(
        UnitPath,
        DaemonServiceDefinitionRenderer.RenderSystemdUserUnit(Scoped),
        cancellationToken);

    private Task ReloadAsync(CancellationToken cancellationToken) =>
        RunSystemctlAsync(["daemon-reload"], cancellationToken);

    private async Task RunSystemctlAsync(
        string[] arguments,
        CancellationToken cancellationToken,
        bool requireSuccess = true)
    {
        _ = await RunAsync(
            SystemctlPath(),
            new[] { "--user" }.Concat(arguments),
            cancellationToken,
            requireSuccess).ConfigureAwait(false);
    }

    private static string SystemctlPath() => File.Exists("/usr/bin/systemctl")
        ? "/usr/bin/systemctl"
        : File.Exists("/bin/systemctl")
            ? "/bin/systemctl"
            : "systemctl";
}
