namespace VibeRails.Daemon.Platform;

internal sealed class LaunchAgentLifecycle(
    ScopedDaemonRegistration scoped,
    IDaemonProcessRunner processRunner,
    Ipc.IDaemonControlClient controlClient)
    : DaemonPlatformLifecycleBase(scoped, processRunner, controlClient)
{
    public override DaemonPlatformKind Platform => DaemonPlatformKind.MacOS;

    private string Domain => $"gui/{Scoped.Identity.UnixUserId
        ?? throw new InvalidOperationException("A Unix user id is required for a LaunchAgent.")}";

    private string PlistDirectory => Path.Combine(
        Scoped.Identity.UserProfileDirectory,
        "Library",
        "LaunchAgents");

    private string PlistPath => Path.Combine(PlistDirectory, Scoped.LaunchAgentLabel + ".plist");

    public override async Task<DaemonRegistrationInspection> InspectAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(PlistPath))
            return new DaemonRegistrationInspection(DaemonRegistrationCondition.NotInstalled);

        var content = await File.ReadAllTextAsync(PlistPath, cancellationToken).ConfigureAwait(false);
        return DaemonServiceDefinitionRenderer.IsCurrentLaunchAgentPlist(content, Scoped)
            ? new DaemonRegistrationInspection(DaemonRegistrationCondition.Current)
            : new DaemonRegistrationInspection(
                DaemonRegistrationCondition.Stale,
                "The LaunchAgent does not match this VibeRails build.");
    }

    public override async Task InstallAsync(CancellationToken cancellationToken)
    {
        EnsurePrivateDirectory(Path.Combine(Scoped.Registration.DataDirectory, "logs"));
        await WritePlistAsync(cancellationToken).ConfigureAwait(false);
        await BootoutAsync(Scoped.LaunchAgentLabel, cancellationToken).ConfigureAwait(false);
        await BootstrapAsync(PlistPath, cancellationToken).ConfigureAwait(false);
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var loaded = await RunLaunchctlAsync(
            ["print", $"{Domain}/{Scoped.LaunchAgentLabel}"],
            cancellationToken,
            requireSuccess: false).ConfigureAwait(false);
        if (loaded.Succeeded)
        {
            _ = await RunLaunchctlAsync(
                ["kickstart", "-k", $"{Domain}/{Scoped.LaunchAgentLabel}"],
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await BootstrapAsync(PlistPath, cancellationToken).ConfigureAwait(false);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _ = await RequestGracefulShutdownAsync(cancellationToken).ConfigureAwait(false);
        await BootoutAsync(Scoped.LaunchAgentLabel, cancellationToken).ConfigureAwait(false);
    }

    public override async Task RepairRegistrationAsync(CancellationToken cancellationToken)
    {
        await BootoutAsync(Scoped.LaunchAgentLabel, cancellationToken).ConfigureAwait(false);
        EnsurePrivateDirectory(Path.Combine(Scoped.Registration.DataDirectory, "logs"));
        await WritePlistAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task UninstallAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken).ConfigureAwait(false);
        if (File.Exists(PlistPath))
            File.Delete(PlistPath);
    }

    public override async Task CleanupLegacyRegistrationsAsync(CancellationToken cancellationToken)
    {
        foreach (var label in Scoped.Registration.LegacyRegistrations.LaunchAgentLabels)
        {
            await BootoutAsync(label, cancellationToken).ConfigureAwait(false);
            var path = Path.Combine(PlistDirectory, label + ".plist");
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private Task WritePlistAsync(CancellationToken cancellationToken) => WritePrivateFileAsync(
        PlistPath,
        DaemonServiceDefinitionRenderer.RenderLaunchAgentPlist(Scoped),
        cancellationToken);

    private async Task BootstrapAsync(string plistPath, CancellationToken cancellationToken) =>
        _ = await RunLaunchctlAsync(["bootstrap", Domain, plistPath], cancellationToken).ConfigureAwait(false);

    private async Task BootoutAsync(string label, CancellationToken cancellationToken) =>
        _ = await RunLaunchctlAsync(
            ["bootout", $"{Domain}/{label}"],
            cancellationToken,
            requireSuccess: false).ConfigureAwait(false);

    private Task<DaemonProcessResult> RunLaunchctlAsync(
        string[] arguments,
        CancellationToken cancellationToken,
        bool requireSuccess = true) =>
        RunAsync("/bin/launchctl", arguments, cancellationToken, requireSuccess);
}
