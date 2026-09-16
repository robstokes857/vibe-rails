using System.Globalization;
using Microsoft.Data.Sqlite;
using VibeRails.Data.Abstractions;

namespace VibeRails.Data.Sqlite;

/// <summary>
/// A consistent copy of a database taken through SQLite's online backup API, written beside the
/// file under <c>backups/</c>. A file copy is the only rollback a breaking migration has.
/// </summary>
internal static class SqliteDatabaseBackup
{
    internal const string BackupDirectoryName = "backups";
    private const long HeadroomBytes = 256L * 1024 * 1024;

    internal static string Create(SqliteConnection source, string reason)
    {
        var full = Path.GetFullPath(source.DataSource);
        var directory = Path.GetDirectoryName(full) ?? ".";
        var backups = Path.Combine(directory, BackupDirectoryName);
        Directory.CreateDirectory(backups);

        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var target = Path.Combine(backups, $"{Path.GetFileNameWithoutExtension(full)}.{stamp}.before-{reason}.db");

        var needed = new FileInfo(full).Length + WalLength(full) + HeadroomBytes;
        var free = AvailableFreeSpace(full);
        if (free is not null && free < needed)
            throw new StorageException(
                $"Not enough free disk to back up '{full}' before migration '{reason}': about {needed / (1024 * 1024)} MB needed, {free / (1024 * 1024)} MB free. Free some space or copy the database elsewhere, then retry. Nothing has been changed.",
                false, new IOException("insufficient disk space for a database backup"));

        using var destination = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = target,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString());
        destination.Open();
        source.BackupDatabase(destination);
        return target;
    }

    private static long WalLength(string databasePath)
    {
        var wal = new FileInfo(databasePath + "-wal");
        return wal.Exists ? wal.Length : 0;
    }

    private static long? AvailableFreeSpace(string path)
    {
        try
        {
            var root = Path.GetPathRoot(path);
            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            // Network or unusual roots: skip the space check rather than refuse the backup.
            return null;
        }
    }
}
