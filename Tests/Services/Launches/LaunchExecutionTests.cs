using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services.Board;
using VibeRails.Services.Cli;
using VibeRails.Services.Jobs;
using VibeRails.Services.Launches;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Launches;

public sealed class LaunchExecutionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "launch-execution-" + Guid.NewGuid().ToString("N"));
    private readonly LaunchStore store;
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    public LaunchExecutionTests()
    {
        Directory.CreateDirectory(root);
        store = new LaunchStore($"Data Source={Path.Combine(root, "launches.db")};Pooling=False");
    }

    [Theory]
    [InlineData(0, false, false, "succeeded")]
    [InlineData(2, false, false, "failed")]
    [InlineData(-1, true, false, "timed-out")]
    [InlineData(-1, false, true, "cancelled")]
    public async Task OneScriptRecordsOwnershipDeadlineSessionAndOutcome(int exit, bool timeout, bool cancelled, string expected)
    {
        var scripts = new Mock<IAutomationScriptService>();
        scripts.Setup(x => x.NormalizeAsync(It.IsAny<string>(), It.IsAny<JobActionRequest>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync((string _, JobActionRequest action, CancellationToken _, bool _) => action with { ApprovedHash = "hash" });
        scripts.Setup(x => x.PrepareAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JobRunActionRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PreparedAutomationScript("python", ["check.py", "literal ; $()"], root, new Dictionary<string, string?>()));
        var wrapper = new Mock<ICliWrapper>();
        wrapper.Setup(x => x.RunAsync(It.IsAny<CliRequest>(), It.IsAny<Func<CliOutputLine, ValueTask>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (CliRequest request, Func<CliOutputLine, ValueTask>? output, CancellationToken token) =>
            {
                Assert.Equal(new[] { "check.py", "literal ; $()" }, request.Arguments);
                Assert.Equal(TimeSpan.FromSeconds(5), request.Timeout);
                await request.OnStarted!(Environment.ProcessId);
                return new CliResult(exit, timeout, cancelled, "", "", TimeSpan.Zero, "script");
            });
        var repo = new Mock<IRepository>();
        var services = new ServiceCollection();
        services.AddSingleton<ILaunchStore>(store);
        services.AddSingleton(scripts.Object);
        services.AddSingleton(wrapper.Object);
        services.AddSingleton(repo.Object);
        services.AddSingleton(Mock.Of<IBoardStore>());
        services.AddScoped<LaunchService>();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<LaunchService>();
        var action = await service.SaveActionAsync(root, null, new("Check", "", "script", new("check.py", JobScriptRuntime.Python, ["literal ; $()"], TimeoutSeconds: 5)), Ct);
        var run = await service.EnqueueAsync("action", action.Id, new(root, root, "manual", "one"), Ct);
        var dispatcher = new LaunchDispatcher(store, service, Mock.Of<ITerminalTabHostService>(MockBehavior.Strict), provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IBoardStore>());
        await dispatcher.DispatchAsync(run.Id, Ct);
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bound.CancelAfter(TimeSpan.FromSeconds(10));
        while (!(run = (await store.GetRunAsync(run.Id, bound.Token))!).IsTerminal) await Task.Delay(25, bound.Token);
        Assert.Equal(expected, run.Status);
        Assert.Equal(Environment.ProcessId, run.ExecutionPid);
        Assert.NotNull(run.DeadlineUtc);
        Assert.NotNull(run.SessionId);
        wrapper.Verify(x => x.RunAsync(It.IsAny<CliRequest>(), It.IsAny<Func<CliOutputLine, ValueTask>?>(), It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(x => x.CompleteSessionAsync(run.SessionId!, exit), Times.Once);
    }

    [Fact]
    public async Task CancellationBetweenActionClaimAndStartReleasesCapacityWithoutCreatingSessions()
    {
        string? cancelRunId = null;
        var validations = 0;
        var scripts = new Mock<IAutomationScriptService>();
        scripts.Setup(x => x.NormalizeAsync(It.IsAny<string>(), It.IsAny<JobActionRequest>(), It.IsAny<CancellationToken>(), true))
            .Returns(async (string _, JobActionRequest action, CancellationToken ct, bool _) =>
            {
                // The dispatcher validates once after claiming; the Action validates again
                // immediately before StartAsync. Cancel at that second boundary.
                if (cancelRunId is not null && ++validations == 2)
                {
                    var claimed = (await store.GetRunAsync(cancelRunId, ct))!;
                    Assert.Equal("claimed", claimed.Status);
                    Assert.Null(claimed.StartedUtc);
                    Assert.True(await store.CancelAsync(claimed.Id, claimed.ProjectPath, ct));
                }
                return action with { ApprovedHash = "hash" };
            });
        scripts.Setup(x => x.PrepareAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JobRunActionRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PreparedAutomationScript("python", ["check.py"], root, new Dictionary<string, string?>()));
        var wrapper = new Mock<ICliWrapper>();
        wrapper.Setup(x => x.RunAsync(It.IsAny<CliRequest>(), It.IsAny<Func<CliOutputLine, ValueTask>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(0, false, false, "", "", TimeSpan.Zero, "script"));
        var repo = new Mock<IRepository>();
        var services = new ServiceCollection();
        services.AddSingleton<ILaunchStore>(store);
        services.AddSingleton(scripts.Object);
        services.AddSingleton(wrapper.Object);
        services.AddSingleton(repo.Object);
        services.AddSingleton(Mock.Of<IBoardStore>());
        services.AddScoped<LaunchService>();
        await using var provider = services.BuildServiceProvider();
        var service = provider.GetRequiredService<LaunchService>();
        var dispatcher = new LaunchDispatcher(store, service, Mock.Of<ITerminalTabHostService>(MockBehavior.Strict),
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<IBoardStore>());
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bound.CancelAfter(TimeSpan.FromSeconds(10));
        for (var i = 0; i <= LaunchDispatcher.Capacity; i++)
        {
            cancelRunId = null;
            var action = await service.SaveActionAsync(root, null,
                new("Check " + i, "", "script", new("check.py", JobScriptRuntime.Python, [])), bound.Token);
            var run = await service.EnqueueAsync("action", action.Id, new(root, root, "manual", "cancel-" + i), bound.Token);
            if (i < LaunchDispatcher.Capacity) cancelRunId = run.Id;
            validations = 0;
            await dispatcher.DispatchAsync(run.Id, bound.Token);
            while (!(run = (await store.GetRunAsync(run.Id, bound.Token))!).IsTerminal) await Task.Delay(10, bound.Token);
            if (cancelRunId is not null)
            {
                Assert.Equal("cancelled", run.Status);
                Assert.Null(run.StartedUtc);
                Assert.Null(run.SessionId);
                Assert.NotNull(run.EndedUtc);
                Assert.Empty(await store.GetActiveRunsAsync(bound.Token));
                Assert.Empty(repo.Invocations);
                Assert.Empty(wrapper.Invocations);
            }
            else Assert.Equal("succeeded", run.Status);
        }
        wrapper.Verify(x => x.RunAsync(It.IsAny<CliRequest>(), It.IsAny<Func<CliOutputLine, ValueTask>?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("unexpected")]
    [InlineData("rejected-then-cancelled")]
    public async Task PreHandoffFailuresReleaseEveryOwnedClaim(string failure)
    {
        var service = new LaunchService(store, Mock.Of<IAutomationScriptService>());
        using var process = Process.GetCurrentProcess();
        for (var i = 0; i < LaunchDispatcher.Capacity; i++)
        {
            var preset = (await service.SavePresetAsync(null, new("Agent " + i, "", "codex", InitialPrompt: "Work"), Ct)).Preset;
            var run = await service.EnqueueAsync("preset", preset.Id, new(root, root, "manual", "failure-" + i), Ct);
            using var request = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            var dispatcherStore = new Mock<ILaunchStore>(MockBehavior.Strict);
            dispatcherStore.Setup(x => x.GetRunAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .Returns((string id, CancellationToken ct) => store.GetRunAsync(id, ct));
            dispatcherStore.Setup(x => x.TryClaimAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
                .Returns(async (string id, int capacity, int pid, DateTime started, CancellationToken ct) =>
                {
                    var claim = await store.TryClaimAsync(id, capacity, pid, started, ct);
                    Assert.NotNull(claim);
                    // The claim has committed before the HTTP request is cancelled.
                    if (failure != "unexpected") request.Cancel();
                    return claim;
                });
            dispatcherStore.Setup(x => x.CompleteAsync(It.IsAny<LaunchClaim>(), It.IsAny<string>(), It.IsAny<int?>(),
                    It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .Returns((LaunchClaim claim, string status, int? exit, string? error, string? evidence, string? verdict, CancellationToken ct) =>
                    store.CompleteAsync(claim, status, exit, error, evidence, verdict, ct));
            var validationStore = new Mock<ILaunchStore>(MockBehavior.Strict);
            validationStore.Setup(x => x.GetPresetAsync(preset.Id, It.IsAny<CancellationToken>()))
                .Returns((string id, CancellationToken ct) => failure switch
                {
                    "unexpected" => Task.FromException<LaunchPreset?>(new InvalidOperationException("Validation unavailable")),
                    "rejected-then-cancelled" => Task.FromException<LaunchPreset?>(new LaunchValidationException("Preset rejected")),
                    _ => store.GetPresetAsync(id, ct)
                });
            var tabs = new Mock<ITerminalTabHostService>(MockBehavior.Strict);
            var dispatcher = new LaunchDispatcher(dispatcherStore.Object,
                new LaunchService(validationStore.Object, Mock.Of<IAutomationScriptService>()), tabs.Object,
                Mock.Of<IServiceScopeFactory>(MockBehavior.Strict), Mock.Of<IBoardStore>(MockBehavior.Strict));
            if (failure == "cancelled")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dispatcher.DispatchAsync(run.Id, request.Token));
            else if (failure == "unexpected")
                await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.DispatchAsync(run.Id, request.Token));
            else
                Assert.Equal("failed", (await dispatcher.DispatchAsync(run.Id, request.Token)).Status);

            var completed = (await store.GetRunAsync(run.Id, Ct))!;
            Assert.Equal(failure == "cancelled" ? "cancelled" : "failed", completed.Status);
            Assert.NotNull(completed.EndedUtc);
            Assert.Null(completed.TabId);
            Assert.Null(completed.SessionId);
            Assert.Empty(tabs.Invocations);
            Assert.Empty(await store.GetActiveRunsAsync(Ct));
        }
        // All three slots, not only the next slot, remain available after repeated failures.
        for (var i = 0; i < LaunchDispatcher.Capacity; i++)
        {
            var preset = (await service.SavePresetAsync(null, new("Next " + i, "", "codex", InitialPrompt: "Work"), Ct)).Preset;
            var run = await service.EnqueueAsync("preset", preset.Id, new(root, root, "manual", "next-" + i), Ct);
            Assert.NotNull(await store.TryClaimAsync(run.Id, LaunchDispatcher.Capacity,
                Environment.ProcessId, process.StartTime.ToUniversalTime(), Ct));
        }
    }

    [Fact]
    public async Task SealedUnstartedPresetRecoversButUnacknowledgedScriptNeedsExplicitResolution()
    {
        var service = new LaunchService(store, Mock.Of<IAutomationScriptService>());
        var preset = (await service.SavePresetAsync(null, new("Agent", "", "codex", InitialPrompt: "Work"), Ct)).Preset;
        var run = await service.EnqueueAsync("preset", preset.Id, new(root, root, "manual", "preset"), Ct);
        var claim = (await store.TryClaimAsync(run.Id, 3, int.MaxValue, DateTime.UtcNow, Ct))!;
        await store.CompleteAsync(claim, "uncertain", ct: Ct);
        var services = new ServiceCollection().AddSingleton(Mock.Of<IRepository>()).BuildServiceProvider();
        await using var ownedServices = services;
        var scheduler = new LaunchScheduler(services.GetRequiredService<IServiceScopeFactory>(), store);
        await scheduler.ReconcileAsync((await store.GetRunAsync(run.Id, Ct))!, services, Ct);
        Assert.Equal("interrupted", (await store.GetRunAsync(run.Id, Ct))!.Status);

        var action = await store.SaveActionAsync(new("script", "Script", "", "script", new("check.py", JobScriptRuntime.Python, []), null, true, 0, default, default), 0, Ct);
        var scriptRun = await store.EnqueueAsync(new(new(LaunchService.ProjectKey(root), root, "manual", "script"), Action: action, ApprovedScriptHash: "hash"), ct: Ct);
        var scriptClaim = (await store.TryClaimAsync(scriptRun.Id, 3, int.MaxValue, DateTime.UtcNow, Ct))!;
        await store.StartAsync(scriptClaim, "session", null, root, int.MaxValue, DateTime.UtcNow, ct: Ct);
        await scheduler.ReconcileAsync((await store.GetRunAsync(scriptRun.Id, Ct))!, services, Ct);
        Assert.Equal("uncertain", (await store.GetRunAsync(scriptRun.Id, Ct))!.Status);
        await Assert.ThrowsAsync<LaunchValidationException>(() => service.ResolveUncertainAsync(root, scriptRun.Id, false, Ct));
        Assert.Equal("interrupted", (await service.ResolveUncertainAsync(root, scriptRun.Id, true, Ct)).Status);
    }

    [Fact]
    public async Task QueuedLaunchPrefersItsOpenProjectAndFallsBackOnlyAfterGrace()
    {
        var service = new LaunchService(store, Mock.Of<IAutomationScriptService>());
        var preset = (await service.SavePresetAsync(null, new("Agent", "", "codex", InitialPrompt: "Work"), Ct)).Preset;
        var run = await service.EnqueueAsync("preset", preset.Id, new(root, root, "manual", "affinity"), Ct);
        var other = Path.Combine(root, "other");
        Assert.True(LaunchScheduler.CanDispatchHere(run, root, [run.ProjectPath], run.QueuedUtc));
        Assert.False(LaunchScheduler.CanDispatchHere(run, other, [], run.QueuedUtc.AddSeconds(29)));
        Assert.False(LaunchScheduler.CanDispatchHere(run, other, [run.ProjectPath], run.QueuedUtc.AddMinutes(1)));
        Assert.True(LaunchScheduler.CanDispatchHere(run, other, [], run.QueuedUtc.AddMinutes(1)));
    }

    public void Dispose() => Directory.Delete(root, true);
}
