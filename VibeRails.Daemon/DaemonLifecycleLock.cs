namespace VibeRails.Daemon;

/// <summary>
/// Cross-process, current-user mutual exclusion for daemon lifecycle mutations (install, start,
/// stop, restart, repair, uninstall). Dashboard tabs, additional root backends, and the release
/// installer's CLI all mutate the same OS registration; interleaving their schtasks/systemctl/
/// launchctl sequences corrupts it. The lock is a FileShare.None file handle in the daemon's data
/// directory, so it works identically for every process role on every platform.
/// </summary>
public sealed class DaemonLifecycleLock : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly FileStream _stream;

    private DaemonLifecycleLock(FileStream stream) => _stream = stream;

    public static async Task<DaemonLifecycleLock> AcquireAsync(
        string applicationId,
        CurrentUserIdentity identity,
        string lockDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var directory = Path.GetFullPath(lockDirectory);
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        var safe = DaemonIdentifier.Normalize(applicationId, nameof(applicationId));
        var path = Path.Combine(directory, $".{safe}-{identity.ScopeKey}-lifecycle.lock");
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var stream = new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                return new DaemonLifecycleLock(stream);
            }
            catch (IOException)
            {
                if (started.Elapsed >= timeout)
                {
                    throw new TimeoutException(
                        "Another VibeRails Demon lifecycle operation is already in progress. " +
                        "Wait for it to finish and retry.");
                }
            }

            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose() => _stream.Dispose();
}
