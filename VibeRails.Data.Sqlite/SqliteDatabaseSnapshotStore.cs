using Microsoft.Data.Sqlite;
using VibeRails.Data.Abstractions;

namespace VibeRails.Data.Sqlite;

/// <summary>Uses SQLite's online backup API, never a filesystem copy of a live database.</summary>
public sealed class SqliteDatabaseSnapshotStore : IDatabaseSnapshotStore
{
    public void CreateSnapshot(string sourcePath, string destinationPath)
    {
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("A snapshot must not overwrite its source.", nameof(destinationPath));
        try
        {
            using var source = SqliteConnectionFactory.Open(new SqliteConnectionStringBuilder
            {
                DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false
            }.ToString(), readOnly: true);
            using var destination = SqliteConnectionFactory.Open(new SqliteConnectionStringBuilder
            {
                // ReadWriteCreate, not ReadWrite: BackupDatabase writes a whole database into the
                // destination, and requiring the caller to pre-create the file is a footgun for
                // every IDatabaseSnapshotStore caller after the first.
                DataSource = destinationPath, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
            }.ToString());
            source.BackupDatabase(destination);
        }
        catch (SqliteException ex)
        {
            throw new StorageException("Could not create the database snapshot.", ex.SqliteErrorCode is 5 or 6, ex);
        }
    }
}
