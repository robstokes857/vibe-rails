namespace VibeRails.Daemon;

/// <summary>
/// Cross-process single-instance guard. Its name contains the current user's scope key so one
/// operating-system account cannot block another account's daemon.
/// </summary>
public sealed class DaemonInstanceGuard : IDisposable
{
    private readonly Semaphore? _semaphore;
    private readonly FileStream? _lockFile;
    private bool _ownsGuard;

    private DaemonInstanceGuard(Semaphore semaphore)
    {
        _semaphore = semaphore;
        _ownsGuard = true;
    }

    private DaemonInstanceGuard(FileStream lockFile)
    {
        _lockFile = lockFile;
        _ownsGuard = true;
    }

    public static DaemonInstanceGuard? TryAcquire(
        string applicationId,
        CurrentUserIdentity identity,
        string? lockDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (OperatingSystem.IsWindows())
        {
            var name = BuildMutexName(applicationId, identity);
            var semaphore = new Semaphore(initialCount: 1, maximumCount: 1, name, out _);
            if (semaphore.WaitOne(0))
                return new DaemonInstanceGuard(semaphore);

            semaphore.Dispose();
            return null;
        }

        var directory = lockDirectory ?? Path.Combine(
            identity.UserProfileDirectory,
            ".local",
            "state",
            DaemonIdentifier.Normalize(applicationId, nameof(applicationId)));
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(
                directory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        try
        {
            var path = BuildLockFilePath(applicationId, identity, directory);
            var stream = new FileStream(
                path,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return new DaemonInstanceGuard(stream);
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Acquire with a bounded retry window. Status probes (dashboard polls, installers) hold the
    /// guard for microseconds; retrying makes it impossible for such a probe to permanently
    /// knock out a starting daemon that would otherwise give up after one failed attempt.
    /// </summary>
    public static async Task<DaemonInstanceGuard?> TryAcquireWithRetryAsync(
        string applicationId,
        CurrentUserIdentity identity,
        string? lockDirectory,
        TimeSpan retryWindow,
        CancellationToken cancellationToken = default)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var guard = TryAcquire(applicationId, identity, lockDirectory);
            if (guard is not null || started.Elapsed >= retryWindow)
                return guard;
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Liveness probe. A real daemon holds the guard persistently, so every attempt fails; a
    /// colliding sibling probe releases within microseconds, so a retry succeeds. Retrying turns
    /// "two probes overlapped" from a false positive into the correct answer.
    /// </summary>
    public static bool IsHeld(
        string applicationId,
        CurrentUserIdentity identity,
        string? lockDirectory = null,
        int attempts = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);
        for (var attempt = 1; ; attempt++)
        {
            using (var probe = TryAcquire(applicationId, identity, lockDirectory))
            {
                if (probe is not null)
                    return false;
            }

            if (attempt >= attempts)
                return true;
            Thread.Sleep(40);
        }
    }

    public static string BuildMutexName(string applicationId, CurrentUserIdentity identity)
    {
        var safe = DaemonIdentifier.Normalize(applicationId, nameof(applicationId));
        var name = $"{safe}-{identity.ScopeKey}-instance";
        return OperatingSystem.IsWindows() ? @"Global\" + name : name;
    }

    public static string BuildLockFilePath(
        string applicationId,
        CurrentUserIdentity identity,
        string lockDirectory)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);
        var safe = DaemonIdentifier.Normalize(applicationId, nameof(applicationId));
        return Path.Combine(Path.GetFullPath(lockDirectory), $".{safe}-{identity.ScopeKey}.lock");
    }

    public void Dispose()
    {
        if (_ownsGuard)
        {
            _ownsGuard = false;
            if (_semaphore is not null)
            {
                try
                {
                    _semaphore.Release();
                }
                catch (SemaphoreFullException)
                {
                    // Another shutdown path already released it.
                }
            }
        }

        _lockFile?.Dispose();
        _semaphore?.Dispose();
    }
}
