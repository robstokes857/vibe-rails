using VibeRails.Daemon.Ipc;

namespace VibeRails.Daemon.Platform;

internal interface IDaemonPlatformLifecycle
{
    DaemonPlatformKind Platform { get; }
    Task<DaemonRegistrationInspection> InspectAsync(CancellationToken cancellationToken);
    Task InstallAsync(CancellationToken cancellationToken);
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task RepairRegistrationAsync(CancellationToken cancellationToken);
    Task UninstallAsync(CancellationToken cancellationToken);
    Task CleanupLegacyRegistrationsAsync(CancellationToken cancellationToken);
}

internal abstract class DaemonPlatformLifecycleBase(
    ScopedDaemonRegistration scoped,
    IDaemonProcessRunner processRunner,
    IDaemonControlClient controlClient) : IDaemonPlatformLifecycle
{
    protected static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    protected static readonly TimeSpan ControlTimeout = TimeSpan.FromSeconds(2);

    protected ScopedDaemonRegistration Scoped { get; } = scoped;
    protected IDaemonProcessRunner ProcessRunner { get; } = processRunner;
    protected IDaemonControlClient ControlClient { get; } = controlClient;

    public abstract DaemonPlatformKind Platform { get; }
    public abstract Task<DaemonRegistrationInspection> InspectAsync(CancellationToken cancellationToken);
    public abstract Task InstallAsync(CancellationToken cancellationToken);
    public abstract Task StartAsync(CancellationToken cancellationToken);
    public abstract Task StopAsync(CancellationToken cancellationToken);
    public abstract Task RepairRegistrationAsync(CancellationToken cancellationToken);
    public abstract Task UninstallAsync(CancellationToken cancellationToken);
    public abstract Task CleanupLegacyRegistrationsAsync(CancellationToken cancellationToken);

    protected async Task<DaemonProcessResult> RunAsync(
        string executable,
        IEnumerable<string> arguments,
        CancellationToken cancellationToken,
        bool requireSuccess = true)
    {
        var request = new DaemonProcessRequest(executable, arguments, CommandTimeout);
        var result = await ProcessRunner.RunAsync(request, cancellationToken).ConfigureAwait(false);
        if (requireSuccess && !result.Succeeded)
        {
            var detail = result.TimedOut
                ? "timed out"
                : $"exited with code {result.ExitCode}";
            var output = string.Join(' ', new[] { result.StandardError, result.StandardOutput }
                .Where(value => !string.IsNullOrWhiteSpace(value))).Trim();
            throw new InvalidOperationException(
                $"{Path.GetFileName(executable)} {detail}. {output}".Trim());
        }
        return result;
    }

    protected async Task<DaemonControlClientResult> RequestGracefulShutdownAsync(
        CancellationToken cancellationToken)
    {
        var result = await ControlClient.SendAsync(
            Scoped.PipeName,
            DaemonControlCommand.Shutdown,
            ControlTimeout,
            cancellationToken).ConfigureAwait(false);
        if (result.Outcome == DaemonControlClientOutcome.ProtocolMismatch)
        {
            // A mismatched daemon cannot be trusted to understand this generation's shutdown
            // semantics; the platform supervisor's stop operation remains authoritative.
            return result;
        }

        return result;
    }

    protected async Task<bool> WaitForInstanceGuardReleaseAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var started = System.Diagnostics.Stopwatch.StartNew();
        while (IsInstanceGuardHeld())
        {
            var remaining = timeout - started.Elapsed;
            if (remaining <= TimeSpan.Zero)
                return false;

            var delay = remaining < TimeSpan.FromMilliseconds(50)
                ? remaining
                : TimeSpan.FromMilliseconds(50);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    protected bool IsInstanceGuardHeld() => DaemonInstanceGuard.IsHeld(
        Scoped.Registration.ApplicationId,
        Scoped.Identity,
        Scoped.Registration.DataDirectory);

    protected static void EnsurePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    protected static async Task WritePrivateFileAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        EnsurePrivateDirectory(Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("The registration file has no parent directory."));
        await File.WriteAllTextAsync(path, content, new System.Text.UTF8Encoding(false), cancellationToken)
            .ConfigureAwait(false);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
