using Microsoft.Data.Sqlite;
using VibeRails.Data.Abstractions;

namespace VibeRails.Data.Sqlite;

internal static class SqliteStorageErrors
{
    public static T Execute<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (SqliteException ex) { throw Translate(ex); }
    }

    public static void Execute(Action operation)
    {
        try { operation(); }
        catch (SqliteException ex) { throw Translate(ex); }
    }

    public static StorageException Translate(SqliteException exception) =>
        new("The storage operation failed.", exception.SqliteErrorCode is 5 or 6, exception);
}
