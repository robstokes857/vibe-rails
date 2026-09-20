using System.Reflection;
using Microsoft.Data.Sqlite;

namespace VibeRails.Data.Sqlite;

/// <summary>
/// What a migration does to builds older than this one. The author declares it; the runner enforces it.
/// </summary>
internal enum MigrationKind
{
    /// <summary>
    /// Older binaries keep working unchanged: a new table, a nullable column, an index, a trigger
    /// that writes only to new tables, a virtual table over a new content table. May run at startup.
    /// </summary>
    Additive,

    /// <summary>
    /// Anything an older binary could misuse afterwards: dropping or renaming, re-pointing a virtual
    /// table's content, changing a trigger or constraint it relies on, or deleting rows. Runs
    /// automatically, with a backup before changing an existing database.
    /// </summary>
    Breaking
}

/// <summary>
/// Tracks whether a database needs a pre-upgrade backup and records migration provenance.
/// Every pending migration runs automatically during normal store initialization. SQLite's
/// writer transaction coordinates competing processes; neither user opt-in nor process counts
/// decide whether a new version can open an existing database.
/// </summary>
internal static class SchemaUpgradePolicy
{
    private sealed record Overrides(bool Backup);

    private static readonly AsyncLocal<Overrides?> _scoped = new();
    private static readonly object _gate = new();
    private static readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _createdByThisProcess = new(StringComparer.OrdinalIgnoreCase);
    private static bool _backupBeforeBreaking = true;

    internal static bool BackupBeforeBreaking => _scoped.Value?.Backup ?? _backupBeforeBreaking;

    /// <summary>Text stored with every receipt so a database can say which build changed it.</summary>
    internal static string AppliedBy { get; } = DescribeThisBinary();

    /// <summary>
    /// Ordinary temporary fixtures skip backup I/O. Upgrade/backup tests enable it through
    /// <see cref="Scope"/>. Migration eligibility is identical in tests and production.
    /// </summary>
    internal static void ConfigureForTests()
    {
        lock (_gate)
        {
            _backupBeforeBreaking = false;
        }
    }

    internal static IDisposable Scope(bool backup)
    {
        var previous = _scoped.Value;
        _scoped.Value = new Overrides(backup);
        return new ScopeRestore(previous);
    }

    /// <summary>
    /// True when the file already held a schema the first time this process touched it. Decided
    /// once per file and remembered, so a fresh file stays "fresh" for the whole run even after the
    /// first migration creates its tables.
    /// </summary>
    internal static bool IsExistingDatabase(SqliteConnection connection)
    {
        var key = connection.DataSource;
        lock (_gate)
        {
            if (_seen.Add(key))
            {
                using var command = connection.CreateCommand();
                command.CommandText =
                    "SELECT 1 FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name<>'SchemaMigrations' LIMIT 1;";
                if (command.ExecuteScalar() is null)
                    _createdByThisProcess.Add(key);
            }
            return !_createdByThisProcess.Contains(key);
        }
    }

    private static string DescribeThisBinary()
    {
        var entry = Assembly.GetEntryAssembly();
        var name = entry?.GetName().Name ?? "unknown";
        var version = entry?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? entry?.GetName().Version?.ToString()
            ?? "?";
        var configuration = entry?.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "?";
        return $"{name} {version} ({configuration}) pid={Environment.ProcessId} host={Environment.MachineName}";
    }

    private sealed class ScopeRestore(Overrides? previous) : IDisposable
    {
        public void Dispose() => _scoped.Value = previous;
    }
}
