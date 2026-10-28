using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;

// Exercise the production resolver and maintenance path without opening user databases.
var directory = Path.Combine(Path.GetTempPath(), "vb-sqlite-vec-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var searchPath = Path.Combine(directory, "search.db");
    var unusedStatePath = Path.Combine(directory, "state.db");
    var store = new SqliteSearchIndexStore(searchPath, unusedStatePath);
    store.Repair();
    store.Repair(); // Reopening the existing vector table must load the extension again.
    if (File.Exists(unusedStatePath))
        throw new InvalidOperationException("Search maintenance unexpectedly opened the source database.");
    Console.WriteLine("sqlite-vec smoke passed: fresh and existing vector storage opened successfully.");
}
finally
{
    SqliteConnection.ClearAllPools();
    Directory.Delete(directory, recursive: true);
}
