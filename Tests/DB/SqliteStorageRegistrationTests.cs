using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using VibeRails.Data.Abstractions;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using Xunit;

namespace Tests.DB;

public sealed class SqliteStorageRegistrationTests
{
    [Fact]
    public async Task StateStores_ResolveThroughFocusedContractsWithOneRepositoryPerScope()
    {
        // A semicolon catches accidental interpolation of paths as connection-string fragments.
        var directory = Path.Combine(Path.GetTempPath(), "viberails-storage;" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "state.db");
            var services = new ServiceCollection();
            services.AddSqliteStateStorage(_ => new SqliteStoragePaths(path));
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            using var first = provider.CreateScope();
            using var second = provider.CreateScope();
            var repository = first.ServiceProvider.GetRequiredService<IRepository>();
            Assert.Same(repository, first.ServiceProvider.GetRequiredService<ISessionStore>());
            Assert.Same(repository, first.ServiceProvider.GetRequiredService<ISessionArchiveReader>());
            Assert.Same(repository, first.ServiceProvider.GetRequiredService<IUserInputStore>());
            Assert.Same(repository, first.ServiceProvider.GetRequiredService<IEnvironmentStore>());
            Assert.Same(repository, first.ServiceProvider.GetRequiredService<ISandboxStore>());
            Assert.Same(repository, first.ServiceProvider.GetRequiredService<IMetadataStore>());
            Assert.Same(repository, first.ServiceProvider.GetRequiredService<IChatSummaryStore>());
            Assert.Same(repository, first.ServiceProvider.GetRequiredService<IEmbeddingProgressStore>());
            Assert.NotSame(repository, second.ServiceProvider.GetRequiredService<IRepository>());
            Assert.Same(first.ServiceProvider.GetRequiredService<IProxyExchangeArchiveReader>(),
                second.ServiceProvider.GetRequiredService<IProxyExchangeArchiveReader>());
            await first.ServiceProvider.GetRequiredService<ISessionStore>()
                .CreateSessionAsync("session", "Codex", null, directory, 1);
            var session = await second.ServiceProvider.GetRequiredService<ISessionStore>()
                .GetSessionByIdAsync("session", TestContext.Current.CancellationToken);
            Assert.NotNull(session);
            Assert.True(File.Exists(path));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }
}
