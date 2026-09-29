using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VibeRails.Utils;

/// <summary>
/// Coordinates settings transactions between upgraded processes that share one file. Older
/// binaries and manual editors do not participate; the atomic replacement still keeps their
/// reads compatible, but cannot prevent an uncoordinated writer from replacing a newer file.
/// </summary>
internal sealed class SettingsFile : IDisposable
{
    private readonly string _path;
    private readonly TimeSpan _lockTimeout;
    private readonly object _gate = new();
    private readonly Mutex _mutex;
    private Settings? _settings;

    internal SettingsFile(string path, TimeSpan? lockTimeout = null)
    {
        _path = Path.GetFullPath(path);
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(5);
        if (_lockTimeout <= TimeSpan.Zero || _lockTimeout.TotalMilliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        var identity = OperatingSystem.IsWindows() ? _path.ToUpperInvariant() : _path;
        var name = "VibeRails.Settings." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        // The same user can have roots in several Windows sessions. The mutex is private to
        // that user, but deliberately not session-local. Its identity is stable across builds.
        _mutex = new Mutex(false, name, new NamedWaitHandleOptions
        {
            CurrentUserOnly = true,
            CurrentSessionOnly = false
        });
        PrivateFilePermissions.EnsureDirectory(Path.GetDirectoryName(_path)!);
        PrivateFilePermissions.EnsureFile(_path);
    }

    internal string DirectoryPath => Path.GetDirectoryName(_path)!;

    /// <summary>
    /// Holds both gates across LoadFresh, field changes, Save and runtime activation. Always
    /// acquire the process gate first, then the OS mutex. Both are reentrant on this thread;
    /// a lease must be disposed synchronously on its acquiring thread, with no await inside.
    /// Timeout is an IOException so account linking retains the received key for retry.
    /// </summary>
    internal IDisposable AcquireWriteLock()
    {
        var started = Stopwatch.GetTimestamp();
        if (!Monitor.TryEnter(_gate, _lockTimeout)) throw Busy();
        try
        {
            var remaining = _lockTimeout - Stopwatch.GetElapsedTime(started);
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            try
            {
                if (!_mutex.WaitOne(remaining)) throw Busy();
            }
            catch (AbandonedMutexException)
            {
                // Ownership is granted after the previous process died. AtomicFile leaves a
                // complete old or new JSON document, so the caller can safely re-read it.
            }
            return new Lease(_gate, _mutex);
        }
        catch
        {
            Monitor.Exit(_gate);
            throw;
        }
    }

    internal Settings Load()
    {
        using var lease = AcquireWriteLock();
        return _settings ?? LoadFreshCore();
    }

    internal Settings LoadFresh()
    {
        using var lease = AcquireWriteLock();
        return LoadFreshCore();
    }

    private Settings LoadFreshCore()
    {
        _settings = null;
        if (!File.Exists(_path))
        {
            var defaults = new Settings();
            SaveCore(defaults);
            return defaults;
        }
        var json = AtomicFile.ReadAllText(_path);
        return _settings = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.Settings)
            ?? throw new InvalidOperationException("Failed to deserialize application settings.");
    }

    // Saving an entire caller-provided snapshot remains supported. A read/modify/write caller
    // must hold AcquireWriteLock before its LoadFresh; locking just Save cannot merge old data.
    internal void Save(Settings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using var lease = AcquireWriteLock();
        SaveCore(settings);
    }

    private void SaveCore(Settings settings)
    {
        AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(settings, ConfigJsonContext.Default.Settings));
        PrivateFilePermissions.EnsureFile(_path);
        _settings = settings;
    }

    private static IOException Busy() => new("Application settings are busy in another operation. Try again.");

    public void Dispose() => _mutex.Dispose();

    private sealed class Lease(object gate, Mutex mutex) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            mutex.ReleaseMutex();
            Monitor.Exit(gate);
            _disposed = true;
        }
    }
}
