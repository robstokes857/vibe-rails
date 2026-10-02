using Microsoft.Data.Sqlite;
using VibeRails.Data.Abstractions;

namespace VibeRails.Data.Sqlite;

/// <summary>Uses SQLite's online backup API, never a filesystem copy of a live database.</summary>
public sealed class SqliteDatabaseSnapshotStore : IDatabaseSnapshotStore
{
    /// <summary>Copies in small page batches under a stable WAL read snapshot; other connections can keep writing.</summary>
    public Task CreateSnapshotAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("A snapshot must not overwrite its source.", nameof(destinationPath));
        cancellationToken.ThrowIfCancellationRequested();
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var secured = new FileStream(destinationPath, options)) { }
        try
        {
            using var source = SqliteConnectionFactory.Open(new SqliteConnectionStringBuilder
            { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString(), readOnly: true);
            using var destination = SqliteConnectionFactory.Open(new SqliteConnectionStringBuilder
            { DataSource = destinationPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            using var transaction = source.BeginTransaction(deferred: true);
            using (var pin = source.CreateCommand())
            { pin.Transaction = transaction; pin.CommandText = "SELECT COUNT(*) FROM sqlite_schema"; pin.ExecuteScalar(); }
            using var backup = SQLitePCL.raw.sqlite3_backup_init(destination.Handle, "main", source.Handle, "main");
            if (backup is null) throw new StorageException("Could not begin database snapshot.", true, new IOException("SQLite backup initialization failed."));
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = SQLitePCL.raw.sqlite3_backup_step(backup, 256);
                if (result == SQLitePCL.raw.SQLITE_DONE) break;
                if (result is not (SQLitePCL.raw.SQLITE_OK or SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED))
                    throw new StorageException("Could not copy database snapshot pages.", false, new IOException($"SQLite backup returned {result}."));
                await Task.Delay(result == SQLitePCL.raw.SQLITE_OK ? 1 : 25, cancellationToken);
            }
        }
        catch (SqliteException ex) { throw new StorageException("Could not create database snapshot.", ex.SqliteErrorCode is 5 or 6, ex); }
    }, cancellationToken);

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
