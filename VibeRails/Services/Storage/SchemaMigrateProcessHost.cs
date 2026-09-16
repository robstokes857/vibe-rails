using Microsoft.Extensions.Configuration;
using Serilog;
using VibeRails.Data.Abstractions;
using VibeRails.Data.Sqlite;
using VibeRails.Services.BertV2;
using VibeRails.Utils;

namespace VibeRails.Services.Storage;

/// <summary>
/// <c>vb --migrate</c>: the one deliberate way to upgrade a database's schema, including breaking
/// steps that ordinary startup refuses. Resolves the same data directory the dashboard would,
/// brings every store's schema current, prints each database's generation and ledger, and exits.
/// The runner still enforces its own rules (no other vb process, backup first), so this host adds
/// no privilege beyond saying "yes, I mean it".
/// </summary>
public static class SchemaMigrateProcessHost
{
    public const string Argument = "--migrate";

    public static bool IsRequested(IReadOnlyList<string> args) =>
        args.Any(argument => argument.Equals(Argument, StringComparison.OrdinalIgnoreCase));

    public static Task<int> RunAsync()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true, reloadOnChange: false)
            .Build();
        var dataDirectory = GlobalRuntimePaths.Initialize(configuration["VibeRails:InstallDirName"]);
        var bert = new BertV2BgeSmallEnSettings();
        var paths = new SqliteStoragePaths(
            ParserConfigs.GetStatePath(),
            Path.Combine(bert.DataDirectory, BertSearchSchema.DatabaseFileName));

        Console.WriteLine($"vb {VersionInfo.Version} {Argument}");
        Console.WriteLine($"Data directory: {dataDirectory} ({PathConstants.DescribeDataDirectoryPolicy()})");
        Console.WriteLine();

        SqliteStorage.AllowBreakingMigrationsForThisProcess();
        try
        {
            var report = SqliteStorage.EnsureAllSchemas(paths);
            foreach (var database in report.Databases)
            {
                Console.WriteLine($"{database.Path}  (generation {database.Generation})");
                foreach (var receipt in database.Receipts)
                    Console.WriteLine($"  {receipt.Component}/{receipt.Version}  {receipt.AppliedUtc}  {receipt.AppliedBy ?? "(applied by an earlier build)"}");
                Console.WriteLine();
            }
            Console.WriteLine("Schema is current.");
            Log.Information("[Migrate] Completed for {DataDirectory}.", dataDirectory);
            return Task.FromResult(0);
        }
        catch (StorageException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Log.Error(ex, "[Migrate] Failed for {DataDirectory}.", dataDirectory);
            return Task.FromResult(1);
        }
    }
}
