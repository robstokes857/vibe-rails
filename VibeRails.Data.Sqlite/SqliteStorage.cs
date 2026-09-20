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
}

/// <summary>The application composition boundary for SQLite; consumers resolve storage interfaces.</summary>
public static class SqliteStorage
{
    public static IServiceCollection AddSqliteStorage(this IServiceCollection services,
        Func<IServiceProvider, SqliteStoragePaths> pathsFactory)
    {
        // Registration ORDER must not decide whether vector search works. AddSqliteStateStorage --
        // which AddAutomationRuntime also calls, with a SqliteStoragePaths carrying no VectorPath --
        // only TryAdds the paths. If that ran first, the TryAdd inside it below would be a no-op
        // here and every vector store would fail at resolve time with "The vector database path has
        // not been configured." This overload is the one that knows about vectors, so it wins
        // outright rather than depending on which extension method was called first.
        services.Replace(ServiceDescriptor.Singleton(pathsFactory));
        services.AddSqliteStateStorage(pathsFactory);
        services.TryAddSingleton<IBertV2VectorStore>(sp => new BertV2VectorStore(VectorPath(sp)));
        services.TryAddSingleton<IBertV2SessionVectorStore>(sp => new BertV2SessionVectorStore(VectorPath(sp)));
        services.TryAddSingleton<IBertSearchDbService>(sp => new BertSearchDbService(
            VectorPath(sp), sp.GetRequiredService<SqliteStoragePaths>().StatePath));
        return services;
    }

    public static IServiceCollection AddSqliteStateStorage(this IServiceCollection services,
        Func<IServiceProvider, SqliteStoragePaths> pathsFactory)
    {
        services.TryAddSingleton(pathsFactory);
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
        services.TryAddScoped<IEmbeddingProgressStore>(sp => sp.GetRequiredService<IRepository>());
        services.TryAddSingleton<IJobStore>(sp => new JobStore(StateConnectionString(sp)));
        services.TryAddSingleton<IBoardStore>(sp => new BoardStore(StateConnectionString(sp)));
        services.TryAddSingleton<ITokenSavingsStore>(sp => new TokenSavingsStore(StateConnectionString(sp)));
        services.TryAddSingleton<ICodeAnalyzerIgnoreStore>(sp => new CodeAnalyzerIgnoreStore(StateConnectionString(sp)));
        services.TryAddSingleton<ILlmExchangeLogStore>(sp => new LlmExchangeLogStore(ConnectionString(
            sp.GetRequiredService<SqliteStoragePaths>().ProxyDatabasePath)));
        services.TryAddSingleton<IDatabaseSnapshotStore, SqliteDatabaseSnapshotStore>();
        services.TryAddSingleton<ISearchIndexMaintenanceStore>(sp => new SqliteSearchIndexMaintenanceStore(StateConnectionString(sp)));
        // The vector path is optional here (AddSqliteStateStorage callers such as
        // AddAutomationRuntime have none) but must be passed when it exists: without it retention
        // clears state.db and leaves the embeddings, so pruned conversations stay searchable.
        services.TryAddSingleton<IDataRetentionStore>(sp => new SqliteDataRetentionStore(StateConnectionString(sp),
            sp.GetRequiredService<SqliteStoragePaths>().ProxyDatabasePath,
            sp.GetRequiredService<SqliteStoragePaths>().VectorPath));
        return services;
    }

    public static IServiceCollection AddSqliteBoardStorage(this IServiceCollection services,
        Func<IServiceProvider, string> statePathFactory)
    {
        services.TryAddSingleton<IBoardStore>(sp => new BoardStore(ConnectionString(statePathFactory(sp))));
        return services;
    }

    public static IServiceCollection AddSqliteJobStorage(this IServiceCollection services,
        Func<IServiceProvider, string> statePathFactory)
    {
        services.TryAddSingleton<IJobStore>(sp => CreateJobStore(statePathFactory(sp)));
        return services;
    }

    public static IJobStore CreateJobStore(string stateDatabasePath) => new JobStore(ConnectionString(stateDatabasePath));

    /// <summary>
    /// Brings every schema this provider owns up to date in one place. Schema snapshot tests use
    /// the same automatic migrations, backups and transaction coordination as store initialization.
    /// </summary>
    public static void EnsureAllSchemas(SqliteStoragePaths paths, Microsoft.Extensions.Logging.ILogger? logger = null)
    {
        var state = ConnectionString(paths.StatePath);
        StateDatabaseSchema.Ensure(state, logger);
        _ = new JobStore(state);
        _ = new BoardStore(state);
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
    }

    private static string StateConnectionString(IServiceProvider provider) =>
        ConnectionString(provider.GetRequiredService<SqliteStoragePaths>().StatePath);

    private static string VectorPath(IServiceProvider provider) =>
        provider.GetRequiredService<SqliteStoragePaths>().VectorPath
            ?? throw new InvalidOperationException("The vector database path has not been configured.");

    private static string ConnectionString(string path) => new SqliteConnectionStringBuilder
    {
        DataSource = path,
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Private
    }.ToString();
}
