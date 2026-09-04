using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VibeRails;
using VibeRails.Jobs;
using VibeRails.Services.Jobs;
using VibeRails.Services.Terminal;
using VibeRails.Services.Terminal.Consumers;
using Xunit;

namespace Tests;

public sealed class MapRegisterServicesProcessRoleTests
{
    public static TheoryData<string[], bool> ProcessRoles => new()
    {
        { [], true },
        { ["--web"], true },
        { ["--git-guard"], true },
        { ["--job-daemon"], false },
        { ["--job-daemon-service", "status", "--json"], false },
        { ["--vs-code-v1"], true },
        { ["--vs-code-v1", "--parent-pid", "42"], false },
        { ["--vs-code-v1", "--parent-pid=42"], false },
        { ["--env", "nightly", "--workdir", @"C:\source\repo"], false },
        // The catastrophic misclassification: a spawned Automation run that counted as an
        // active root would start scheduling — every job terminal enqueueing more job terminals.
        { ["--env", "nightly", "--workdir", @"C:\source\repo", "--job-run", "run-1"], false },
        // Script-only Automation runs omit --env but are still execution children, never roots.
        { ["--workdir", @"C:\source\repo", "--job-run", "run-1"], false },
    };

    [Theory]
    [MemberData(nameof(ProcessRoles))]
    public void IsActiveRootBackendProcess_ClassifiesSupportedProcessRoles(
        string[] args,
        bool expected)
    {
        Assert.Equal(expected, MapRegisterServices.IsActiveRootBackendProcess(args));
    }

    /// <summary>
    /// Route mapping reads this singleton instead of re-deriving the role from
    /// <see cref="Environment.GetCommandLineArgs"/>, which is a different array than the one
    /// Register is handed. Publishing it is what keeps "is this route mapped" and "is its service
    /// registered" from ever being able to disagree.
    /// </summary>
    [Theory]
    [MemberData(nameof(ProcessRoles))]
    public void Register_PublishesTheResolvedProcessRole(string[] args, bool expectedActiveRoot)
    {
        var services = new ServiceCollection();
        MapRegisterServices.Register(services, args, "http://127.0.0.1:12345");
        using var provider = services.BuildServiceProvider();

        var role = provider.GetRequiredService<ProcessRole>();

        Assert.Equal(expectedActiveRoot, role.IsActiveRootBackend);
        Assert.Equal(MapRegisterServices.IsTerminalTabChildProcess(args), role.IsTerminalTabChild);
    }

    [Theory]
    [MemberData(nameof(ProcessRoles))]
    public void Register_HostsSessionDataDrainOnlyInActiveRootProcesses(
        string[] args,
        bool expectedActiveRoot)
    {
        var services = new ServiceCollection();

        MapRegisterServices.Register(services, args, "http://127.0.0.1:12345");

        var registrations = services.Count(descriptor =>
            descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(SessionDataDrainJob));
        Assert.Equal(expectedActiveRoot ? 1 : 0, registrations);
    }

    [Fact]
    public async Task RetiredJobTick_IsRecognizedButPerformsNoWork()
    {
        Assert.True(JobTickProcessHost.IsRequested(["--job-tick"]));
        Assert.Equal(0, await JobTickProcessHost.RunAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void JobDaemon_IsRecognizedOnlyBeforeThePassthroughSeparator()
    {
        Assert.True(JobDaemonProcessHost.IsRequested(["--job-daemon"]));
        Assert.False(JobDaemonProcessHost.IsRequested(["--env", "nightly", "--", "--job-daemon"]));
    }

    [Fact]
    public void JobDaemonMaintenance_IsRecognizedOnlyBeforeThePassthroughSeparator()
    {
        Assert.True(JobDaemonMaintenanceProcessHost.IsRequested(["--job-daemon-service", "status", "--json"]));
        Assert.False(JobDaemonMaintenanceProcessHost.IsRequested(
            ["--env", "nightly", "--", "--job-daemon-service", "status"]));
    }

    [Fact]
    public void AutomationConsumer_IsOneLazySingletonAcrossScopes()
    {
        var services = new ServiceCollection();
        MapRegisterServices.Register(
            services,
            ["--env", "nightly", "--job-run", "run-1"],
            "http://127.0.0.1:12345");
        using var provider = services.BuildServiceProvider();

        var rootInstance = provider.GetRequiredService<IAutomationConsumer>();
        using var scope = provider.CreateScope();
        var scopedInstance = scope.ServiceProvider.GetRequiredService<IAutomationConsumer>();

        Assert.Same(rootInstance, scopedInstance);
        Assert.IsType<AutomationConsumer>(rootInstance);
        Assert.False(((AutomationConsumer)rootInstance).TimerStarted);
    }
}
