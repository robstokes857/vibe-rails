using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VibeRails.Services.Cli;
using VibeRails.Services.Jobs;
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

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        using var scope = provider.CreateScope();

        Assert.IsType<JobLaunchService>(scope.ServiceProvider.GetRequiredService<IJobLaunchService>());

        var concreteScheduler = provider.GetRequiredService<JobSchedulerHostedService>();
        Assert.Same(concreteScheduler, provider.GetRequiredService<IJobScheduler>());
        Assert.Same(
            concreteScheduler,
            Assert.Single(provider.GetServices<IHostedService>().OfType<JobSchedulerHostedService>()));
        Assert.Same(
            provider.GetRequiredService<JobSchedulerHealth>(),
            provider.GetRequiredService<JobSchedulerHealth>());
        Assert.Single(provider.GetServices<ICliWrapper>());
        Assert.IsType<AutomationScriptService>(
            provider.GetRequiredService<IAutomationScriptService>());
        Assert.IsType<JobProcessLauncher>(
            provider.GetRequiredService<IJobProcessLauncher>());
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
