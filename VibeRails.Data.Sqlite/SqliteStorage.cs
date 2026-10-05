using VibeRails.DTOs;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using VibeRails.Data.Abstractions;
using VibeRails.DB;
using VibeRails.Services.BertV2;
using VibeRails.Services.Board;

namespace VibeRails.Data.Sqlite;

/// <summary>File locations supplied by the host; connection settings belong to the provider.</summary>
public sealed record SqliteStoragePaths(string StatePath, string? VectorPath = null, string? ProxyPath = null)
{
    internal string ProxyDatabasePath => ProxyPath ?? Path.Combine(Path.GetDirectoryName(StatePath) ?? ".", "proxy_exchanges.db");
    public string BoardDatabasePath => Path.Combine(Path.GetDirectoryName(StatePath) ?? ".", "board.db");
    public string SearchDatabasePath => Path.Combine(Path.GetDirectoryName(StatePath) ?? ".", "search.db");
}

/// <summary>The application composition boundary for SQLite; consumers resolve storage interfaces.</summary>
public static class SqliteStorage
{
    public static IServiceCollection AddSqliteStorage(this IServiceCollection services,
        Func<IServiceProvider, SqliteStoragePaths> pathsFactory)
    {
        // The full host owns the shared per-user search component. State-only hosts need
        // neither a model nor a search writer; registration order must not change these paths.
        services.Replace(ServiceDescriptor.Singleton(pathsFactory));
        services.TryAddSingleton<VibeRails.Data.Replay.IReplayStore, Replay.ReplayStore>();
        services.AddSqliteStateStorage(pathsFactory);
        services.TryAddSingleton<ISearchIndexStore>(sp => new SqliteSearchIndexStore(
            sp.GetRequiredService<SqliteStoragePaths>().SearchDatabasePath, sp.GetRequiredService<SqliteStoragePaths>().StatePath));
        services.TryAddSingleton<IBertSearchDbService, BertSearchDbService>();
        return services;
    }

    public static IServiceCollection AddSqliteStateStorage(this IServiceCollection services,
        Func<IServiceProvider, SqliteStoragePaths> pathsFactory, bool consumeBoardEvents = false)
    {
        services.TryAddSingleton(pathsFactory);
        // Resolve this opt-in when the JobStore is constructed, not when its factory is registered.
        // The full host can register state storage before the scheduler composition runs.
        if (consumeBoardEvents)
            services.TryAddSingleton<BoardAutomationEventSource>(sp => new(sp.GetRequiredService<IBoardStore>()));
        // The logger is not optional in practice: this reader degrades to coverage "unavailable"
        // on a lock or read error, and without the logger that degradation is completely silent.
        services.TryAddSingleton<IProxyExchangeArchiveReader>(sp => new SqliteProxyExchangeArchiveReader(
            sp.GetRequiredService<SqliteStoragePaths>().ProxyDatabasePath,
            sp.GetService<ILogger<SqliteProxyExchangeArchiveReader>>()));
        services.TryAddScoped<IRepository>(sp => new Repository(StateConnectionString(sp),
            sp.GetService<ILogger<Repository>>(), sp.GetRequiredService<IProxyExchangeArchiveReader>()));
        services.TryAddScoped<ISessionStore>(sp => sp.GetRequiredService<IRepository>());
        services.TryAddScoped<ISessionArchiveReader>(sp => sp.GetRequiredService<IRepository>());
        services.TryAddScoped<IUserInputStore>(sp => sp.GetRequiredService<IRepository>());
        services.TryAddScoped<IEnvironmentStore>(sp => sp.GetRequiredService<IRepository>());
        services.TryAddScoped<ISandboxStore>(sp => sp.GetRequiredService<IRepository>());
        services.TryAddScoped<IMetadataStore>(sp => sp.GetRequiredService<IRepository>());
        services.TryAddScoped<IChatSummaryStore>(sp => sp.GetRequiredService<IRepository>());
        services.TryAddSingleton<IBoardStore>(sp => CreateBoardStore(sp.GetRequiredService<SqliteStoragePaths>().StatePath));
        services.TryAddSingleton<IJobStore>(sp => new JobStore(StateConnectionString(sp), sp.GetService<BoardAutomationEventSource>()?.Store, sp.GetService<IReviewRunSnapshotFactory>()));
        services.TryAddSingleton<ITokenSavingsStore>(sp => new TokenSavingsStore(StateConnectionString(sp)));
        services.TryAddSingleton<ICodeAnalyzerIgnoreStore>(sp => new CodeAnalyzerIgnoreStore(StateConnectionString(sp)));
        services.TryAddSingleton<ILlmExchangeLogStore>(sp => new LlmExchangeLogStore(ConnectionString(
            sp.GetRequiredService<SqliteStoragePaths>().ProxyDatabasePath)));
        services.TryAddSingleton<IDatabaseSnapshotStore, SqliteDatabaseSnapshotStore>();
        // Search reconciliation propagates retention from canonical source records. Retired
        // standalone vector databases are retained; current roots no longer maintain them.
        services.TryAddSingleton<IDataRetentionStore>(sp => new SqliteDataRetentionStore(StateConnectionString(sp),
            sp.GetRequiredService<SqliteStoragePaths>().ProxyDatabasePath,
            vectorDatabasePath: null));
        return services;
    }

    public static IServiceCollection AddSqliteBoardStorage(this IServiceCollection services,
        Func<IServiceProvider, string> statePathFactory)
    {
        services.TryAddSingleton<IBoardStore>(sp => CreateBoardStore(statePathFactory(sp)));
        return services;
    }

    public static IServiceCollection AddSqliteJobStorage(this IServiceCollection services,
        Func<IServiceProvider, string> statePathFactory)
    {
        services.TryAddSingleton<IJobStore>(sp => new JobStore(ConnectionString(statePathFactory(sp)), sp.GetService<BoardAutomationEventSource>()?.Store, sp.GetService<IReviewRunSnapshotFactory>()));
        return services;
    }

    // Commit hooks only enqueue local Jobs. Board health/schema must never gate this path.
    public static IJobStore CreateJobStore(string stateDatabasePath) =>
        new JobStore(ConnectionString(stateDatabasePath));

    private sealed record BoardAutomationEventSource(IBoardStore Store);

    /// <summary>
    /// Uses the same board.db for dashboard, stdio MCP and the lane Automation scheduler. Legacy Board tables
    /// in state.db are deliberately left untouched and are never copied, read or written here.
    /// </summary>
    public static IBoardStore CreateBoardStore(string stateDatabasePath) =>
        new BoardStore(ConnectionString(new SqliteStoragePaths(stateDatabasePath).BoardDatabasePath),
            ConnectionString(stateDatabasePath));

    /// <summary>
    /// Brings every schema this provider owns up to date in one place. Schema snapshot tests use
    /// the same automatic migrations, backups and transaction coordination as store initialization.
    /// </summary>
    public static void EnsureAllSchemas(SqliteStoragePaths paths, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var state = ConnectionString(paths.StatePath);
        StateDatabaseSchema.Ensure(state, logger);
        _ = new JobStore(state);
        _ = CreateBoardStore(paths.StatePath);
        using (var connection = SqliteConnectionFactory.Open(state))
        {
            TokenSavingsStore.EnsureSchema(connection);
            CodeAnalyzerIgnoreStore.EnsureSchema(connection);
            CompressionCapturesSchema.EnsureSchema(connection);
        }
        using (var connection = SqliteConnectionFactory.Open(ConnectionString(paths.ProxyDatabasePath)))
            LlmExchangeLogStore.EnsureSchema(connection);

        if (paths.VectorPath is not null)
            BertVectorDatabase.Initialize(paths.VectorPath);
        SearchDatabaseSchema.Ensure(paths.SearchDatabasePath);
        SearchDatabaseSchema.EnsureVectors(paths.SearchDatabasePath);
    }

    private static string StateConnectionString(IServiceProvider provider) =>
        ConnectionString(provider.GetRequiredService<SqliteStoragePaths>().StatePath);

    private static string ConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private
    }.ToString();
}
