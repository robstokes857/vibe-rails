using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using VibeRails.DB;
using VibeRails.Services.Cli;
using VibeRails.Services.Jobs;
using VibeRails.Services.Terminal;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Jobs;

[Collection(JobSchedulerHostedServiceTestCollection.Name)]
public sealed class AutomationRuntimeServiceCollectionExtensionsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"viberails-automation-runtime-{Guid.NewGuid():N}");
    private readonly string _statePath;
    private readonly string _previousStatePath = ParserConfigs.GetStatePath();
    private readonly string _previousEnvironmentPath = ParserConfigs.GetEnvPath();
    private readonly string _previousSandboxPath = ParserConfigs.GetSandboxPath();
    private readonly string _previousHistoryPath = ParserConfigs.GetHistoryPath();
    private readonly string _previousConfigPath = ParserConfigs.GetConfigPath();

    public AutomationRuntimeServiceCollectionExtensionsTests()
    {
        _statePath = Path.Combine(_root, "state.db");
        var environmentPath = Path.Combine(_root, "envs");
        var sandboxPath = Path.Combine(_root, "sandboxes");
        var historyPath = Path.Combine(_root, "history");
        Directory.CreateDirectory(environmentPath);
        Directory.CreateDirectory(sandboxPath);
        Directory.CreateDirectory(historyPath);

        ParserConfigs.SetStatePath(_statePath);
        ParserConfigs.SetEnvPath(environmentPath);
        ParserConfigs.SetSandboxPath(sandboxPath);
        ParserConfigs.SetHistoryPath(historyPath);
        ParserConfigs.SetConfigPath(Path.Combine(_root, "config.json"));
    }

    [Fact]
    public void AddAutomationRuntime_ResolvesTheMinimalLaunchGraph_AndHostsOneSchedulerInstance()
    {
        var services = new ServiceCollection();
        services.AddAutomationRuntime(hostScheduler: true);
        // The root host supplies its terminal manager; process-only hosts omit the tab launcher.
        services.AddSingleton(Mock.Of<ITerminalTabHostService>());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<VibeRails.Services.Launches.LaunchDispatcher>());
        Assert.Null(provider.GetService<IJobScheduler>());
        Assert.Null(scope.ServiceProvider.GetService<IJobLaunchService>());
        Assert.Empty(provider.GetServices<IHostedService>().OfType<JobSchedulerHostedService>());
        Assert.Single(provider.GetServices<IHostedService>().OfType<VibeRails.Services.Launches.LaunchScheduler>());
        Assert.True(File.Exists(Path.Combine(_root, "launches.db")));
        Assert.Single(provider.GetServices<ICliWrapper>());
        Assert.IsType<AutomationScriptService>(provider.GetRequiredService<IAutomationScriptService>());
    }

    [Fact]
    public async Task NonSchedulingRuntime_DoesNotRequireAUsableBoardDatabase()
    {
        var boardPath = Path.Combine(_root, "board.db");
        await File.WriteAllTextAsync(boardPath, "Damaged Board file", TestContext.Current.CancellationToken);
        var services = new ServiceCollection();
        services.AddAutomationRuntime(hostScheduler: false);
        using var provider = services.BuildServiceProvider();
        var jobs = provider.GetRequiredService<IJobStore>();
        Assert.Equal(0, await jobs.CountEnabledJobsAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Damaged Board file", await File.ReadAllTextAsync(boardPath, TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        ParserConfigs.SetStatePath(_previousStatePath);
        ParserConfigs.SetEnvPath(_previousEnvironmentPath);
        ParserConfigs.SetSandboxPath(_previousSandboxPath);
        ParserConfigs.SetHistoryPath(_previousHistoryPath);
        ParserConfigs.SetConfigPath(_previousConfigPath);

        var connectionString =
            $"Data Source={_statePath};Mode=ReadWriteCreate;Cache=Shared";
        SqliteConnection.ClearPool(new SqliteConnection(connectionString));
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best-effort temporary test cleanup */ }
    }
}
