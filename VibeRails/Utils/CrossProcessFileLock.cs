namespace VibeRails.Utils;

/// <summary>
/// Holds an operating-system file-share lock for cross-process coordination.
///
/// The empty lock file is intentionally persistent. Deleting it on release would introduce an
/// inode/path race where one process still holds the old file while another creates and locks a
/// replacement. Closing the stream releases the OS lock after normal exit or a process crash.
/// </summary>
internal sealed class CrossProcessFileLock : IDisposable
{
    private readonly FileStream _stream;

    private CrossProcessFileLock(FileStream stream)
    {
        _stream = stream;
    }

    /// <summary>
    /// Attempts to acquire an exclusive lock without waiting. Returns <see langword="null"/> when
    /// another process (or another operation in this process) already owns the lock.
    /// </summary>
    internal static CrossProcessFileLock? TryAcquire(string lockPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(lockPath))
            ?? throw new InvalidOperationException("The lock file has no parent directory.");
        // The state directory normally already exists. Do not chmod an operator-supplied parent
        // directory here; only the coordination file itself belongs to this helper.
        Directory.CreateDirectory(directory);

        FileStream? stream = null;
        try
        {
            // FileShare.None is implemented by the runtime with OS file locking on every
            // supported platform. Keeping the handle open holds the lock across async awaits
            // without the thread affinity of a named Mutex.
            stream = new FileStream(
                lockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            PrivateFilePermissions.EnsureFile(lockPath);
            return new CrossProcessFileLock(stream);
        }
        catch (IOException)
        {
            stream?.Dispose();
            return null;
        }
    }

    /// <summary>
    /// Places a coordination lock beside the shared state database so installations with a custom
    /// state path still coordinate on the same OS object.
    /// </summary>
    internal static string BesideStateDatabase(string? statePath, string lockFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFileName);

        var directory = string.IsNullOrWhiteSpace(statePath)
            ? PathConstants.GetInstallDirPath()
            : Path.GetDirectoryName(Path.GetFullPath(statePath));
        if (string.IsNullOrWhiteSpace(directory))
            directory = PathConstants.GetInstallDirPath();

        return Path.Combine(directory, lockFileName);
    }

    public void Dispose()
    {
        _stream.Dispose();
    }
}
