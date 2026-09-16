using System.Diagnostics;
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
    /// table's content, changing a trigger or constraint it relies on, or deleting rows. Never runs
    /// automatically against a database that existed before this process started.
    /// </summary>
    Breaking
}

/// <summary>
/// Decides whether a breaking migration may touch a database that already existed before this
/// process started. Files this process created are never guarded: nothing in them was written by
/// an older binary. The production rule is simple -- breaking migrations run only from
/// <c>vb --migrate</c>, with no other vb process alive and a backup taken first -- and tests relax
/// it per async scope so ordinary fixtures never trip it while the guard tests still can.
/// </summary>
internal static class SchemaUpgradePolicy
{
    internal const string MigrateEnvironmentVariable = "VIBE_RAILS_MIGRATE";
    internal const string ProcessName = "vb";

    private sealed record Overrides(bool? AllowBreaking, Func<IReadOnlyList<int>>? OtherProcesses, bool? Backup);

    private static readonly AsyncLocal<Overrides?> _scoped = new();
    private static readonly object _gate = new();
    private static readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> _createdByThisProcess = new(StringComparer.OrdinalIgnoreCase);
    private static bool _allowBreaking;
    private static Func<IReadOnlyList<int>> _otherProcesses = ProbeOtherVibeRailsProcesses;
    private static bool _backupBeforeBreaking = true;

    /// <summary>Called by the <c>vb --migrate</c> host and nothing else in production.</summary>
    internal static void AllowBreakingMigrationsForThisProcess()
    {
        lock (_gate)
            _allowBreaking = true;
    }

    internal static bool BreakingMigrationsAllowed =>
        _scoped.Value?.AllowBreaking
        ?? (_allowBreaking || IsTruthy(Environment.GetEnvironmentVariable(MigrateEnvironmentVariable)));

    internal static IReadOnlyList<int> OtherVibeRailsProcesses() => (_scoped.Value?.OtherProcesses ?? _otherProcesses)();

    internal static bool BackupBeforeBreaking => _scoped.Value?.Backup ?? _backupBeforeBreaking;

    /// <summary>Text stored with every receipt so a database can say which build changed it.</summary>
    internal static string AppliedBy { get; } = DescribeThisBinary();

    /// <summary>
    /// Process-wide defaults for the test assembly: fixtures that adopt a legacy schema on a temp
    /// file must not need a --migrate flag, a process probe, or a backup. Guard tests opt back in
    /// through <see cref="Scope"/>, which wins over these defaults inside its async flow.
    /// </summary>
    internal static void ConfigureForTests()
    {
        lock (_gate)
        {
            _allowBreaking = true;
            _otherProcesses = () => [];
            _backupBeforeBreaking = false;
        }
    }

    internal static IDisposable Scope(bool? allowBreaking = null, IReadOnlyList<int>? otherProcesses = null, bool? backup = null)
    {
        var previous = _scoped.Value;
        _scoped.Value = new Overrides(allowBreaking, otherProcesses is null ? null : () => otherProcesses, backup);
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

    private static IReadOnlyList<int> ProbeOtherVibeRailsProcesses()
    {
        var self = Environment.ProcessId;
        var others = new List<int>();
        foreach (var process in Process.GetProcessesByName(ProcessName))
        {
            using (process)
            {
                if (process.Id != self)
                    others.Add(process.Id);
            }
        }
        others.Sort();
        return others;
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

    private static bool IsTruthy(string? value) =>
        value is not null && value.Trim().ToLowerInvariant() is "1" or "true" or "yes";

    private sealed class ScopeRestore(Overrides? previous) : IDisposable
    {
        public void Dispose() => _scoped.Value = previous;
    }
}
