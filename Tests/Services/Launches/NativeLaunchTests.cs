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
using VibeRails.Services.LlmClis.Launchers;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Launches;

public sealed class NativeLaunchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "native-launch-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LaunchStore store;
    private readonly Mock<IAutomationScriptService> scripts = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public NativeLaunchTests()
    {
        Directory.CreateDirectory(root);
        store = new LaunchStore($"Data Source={Path.Combine(root, "launches.db")};Pooling=False");
        scripts.Setup(x => x.NormalizeAsync(It.IsAny<string>(), It.IsAny<JobActionRequest>(), It.IsAny<CancellationToken>(), true))
            .ReturnsAsync((string _, JobActionRequest action, CancellationToken _, bool _) => action with { ApprovedHash = "hash" });
        scripts.Setup(x => x.PrepareAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<JobRunActionRecord>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PreparedAutomationScript("python", ["check.py", "literal ; $()"], root, new Dictionary<string, string?>()));
    }

    private ServiceProvider Services(ICliWrapper? cli = null) => new ServiceCollection()
        .AddSingleton<ILaunchStore>(store).AddSingleton(scripts.Object)
        .AddSingleton(Mock.Of<IRepository>()).AddSingleton(Mock.Of<IBoardStore>())
        .AddSingleton(cli ?? Mock.Of<ICliWrapper>(MockBehavior.Strict))
        .AddScoped<LaunchService>().BuildServiceProvider();

    private async Task<LaunchRun> Enqueue(LaunchService service, bool script)
    {
        var id = script
            ? (await service.SaveActionAsync(root, null, new("Script", "", "script", new("check.py", JobScriptRuntime.Python, [])), Ct)).Id
            : (await service.SavePresetAsync(null, new("Agent", "", "codex", InitialPrompt: "Work"), Ct)).Preset.Id;
        return await service.EnqueueAsync(script ? "action" : "preset", id,
            new(root, root, "manual", Guid.NewGuid().ToString("N"), ComposedPrompt: "Work", ContextIncluded: true, NativeTerminal: script), Ct);
    }

    [Theory]
    [InlineData("manual", null, false, true)]
    [InlineData("manual", null, true, true)]
    [InlineData("schedule", null, false, true)]
    [InlineData("before-commit", null, false, true)]
    [InlineData("after-commit", null, false, true)]
    [InlineData("board", null, true, false)]
    [InlineData("board", "card", true, false)]
    [InlineData("board-lane", "card", true, false)]
    [InlineData("manual", "card", true, false)]
    public async Task PresetDestinationFollowsBoardOriginInsteadOfCallerPreference(
        string trigger, string? card, bool requestedNative, bool expectedNative)
    {
        var service = new LaunchService(store, scripts.Object);
        var preset = (await service.SavePresetAsync(null, new("Agent", "", "codex", InitialPrompt: "Work"), Ct)).Preset;
        var run = await service.EnqueueAsync("preset", preset.Id,
            new(root, root, trigger, "destination", CardId: card, NativeTerminal: requestedNative), Ct);
        Assert.Equal(expectedNative, run.Snapshot.Context.NativeTerminal);
        Assert.Equal(expectedNative, (await store.GetRunAsync(run.Id, Ct))!.Snapshot.Context.NativeTerminal);
    }

    [Fact]
    public async Task QuickLaunchAlsoDefaultsToNativeButScriptPresentationIsPreserved()
    {
        var service = new LaunchService(store, scripts.Object);
        var preset = (await service.SavePresetAsync(null, new("Quick launch", "", "codex"), Ct)).Preset;
        var quick = await service.EnqueueAsync("preset", preset.Id, new(root, root, "manual", "quick"), Ct);
        Assert.True(quick.Snapshot.Context.NativeTerminal);
        var action = await service.SaveActionAsync(root, null, new("Script", "", "script", new("check.py", JobScriptRuntime.Python, [])), Ct);
        var script = await service.EnqueueAsync("action", action.Id, new(root, root, "manual", "script"), Ct);
        Assert.False(script.Snapshot.Context.NativeTerminal);
    }

    [Theory]
    [InlineData("manual", false, true)]
    [InlineData("board", true, false)]
    public async Task RetryUsesCurrentDestinationPolicyWithoutRewritingOriginalInputs(
        string trigger, bool originalNative, bool expectedNative)
    {
        var service = new LaunchService(store, scripts.Object);
        var preset = (await service.SavePresetAsync(null, new("Agent", "", "codex", InitialPrompt: "Original prompt"), Ct)).Preset;
        // A retained snapshot from an older version may have used the other presentation.
        var original = await store.EnqueueAsync(new(new(LaunchService.ProjectKey(root), root, trigger, "original",
            NativeTerminal: originalNative), preset), ct: Ct);
        var claim = (await store.TryClaimAsync(original.Id, 3, Environment.ProcessId, DateTime.UtcNow, Ct))!;
        await store.CompleteAsync(claim, "failed", ct: Ct);
        await service.SavePresetAsync(preset.Id, new("Agent", "", "codex", InitialPrompt: "Changed prompt", ExpectedRevision: preset.Revision), Ct);

        var retry = await service.RetryAsync(root, original.Id, Ct);

        Assert.Equal(expectedNative, retry.Snapshot.Context.NativeTerminal);
        Assert.Equal(original.Id, retry.SourceRunId);
        Assert.Equal("Original prompt", retry.Snapshot.Preset!.InitialPrompt);
        Assert.Equal(originalNative, (await store.GetRunAsync(original.Id, Ct))!.Snapshot.Context.NativeTerminal);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task NativeDispatchOpensOneOsWindowWithoutCreatingATerminalTab(bool script, bool opens)
    {
        await using var services = Services();
        var service = services.GetRequiredService<LaunchService>();
        var run = await Enqueue(service, script);
        var native = new Mock<IJobProcessLauncher>(MockBehavior.Strict);
        native.Setup(x => x.Launch(root, It.IsAny<IReadOnlyList<string>>(), false))
            .Returns((string _, IReadOnlyList<string> args, bool _) =>
            {
                Assert.Equal(new[] { "--launch-run", run.Id, "--workdir", root }, args);
                return new LaunchResult(opens, opens ? "Opened" : "Terminal executable unavailable");
            });
        var dispatcher = new LaunchDispatcher(store, service, Mock.Of<ITerminalTabHostService>(MockBehavior.Strict),
            services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<IBoardStore>(), native.Object);
        var launched = await dispatcher.DispatchAsync(run.Id, Ct);
        Assert.Equal(opens ? "claimed" : "failed", launched.Status);
        Assert.Null(launched.TabId);
        if (!opens) Assert.Contains("unavailable", launched.Error);
        await dispatcher.DispatchAsync(run.Id, Ct);
        native.Verify(x => x.Launch(root, It.IsAny<IReadOnlyList<string>>(), false), Times.Once);
    }

    [Theory]
    [InlineData(0, "succeeded")]
    [InlineData(2, "failed")]
    public async Task NativeChildExecutesAndRecordsScriptInsteadOfOnlyOpeningAWindow(int exit, string status)
    {
        var cli = new Mock<ICliWrapper>(MockBehavior.Strict);
        cli.Setup(x => x.RunAsync(It.IsAny<CliRequest>(), It.IsAny<Func<CliOutputLine, ValueTask>>(), It.IsAny<CancellationToken>()))
            .Returns(async (CliRequest request, Func<CliOutputLine, ValueTask> output, CancellationToken _) =>
            {
                Assert.Equal(new[] { "check.py", "literal ; $()" }, request.Arguments);
                await request.OnStarted!(Environment.ProcessId);
                await output(new(false, "native output", TimeSpan.Zero));
                return new CliResult(exit, false, false, "", "", TimeSpan.Zero, "script");
            });
        await using var services = Services(cli.Object);
        var run = await Enqueue(services.GetRequiredService<LaunchService>(), true);
        using var process = Process.GetCurrentProcess();
        Assert.NotNull(await store.TryClaimAsync(run.Id, 3, Environment.ProcessId, process.StartTime.ToUniversalTime(), Ct));
        Assert.Equal(exit, await NativeLaunchRunner.RunAsync(run.Id, services, Ct));
        var done = (await store.GetRunAsync(run.Id, Ct))!;
        Assert.Equal(status, done.Status);
        Assert.NotNull(done.SessionId);
        Assert.Equal(Environment.ProcessId, done.OwnerPid);
        Assert.Equal(Environment.ProcessId, done.ExecutionPid);
        Assert.Null(done.TabId);
        Assert.Equal(1, await NativeLaunchRunner.RunAsync(run.Id, services, Ct));
        cli.Verify(x => x.RunAsync(It.IsAny<CliRequest>(), It.IsAny<Func<CliOutputLine, ValueTask>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingNativeAcknowledgementSealsTheClaimBeforeReleasingIt(bool script)
    {
        await using var services = Services();
        var run = await Enqueue(services.GetRequiredService<LaunchService>(), script);
        using var process = Process.GetCurrentProcess();
        var claim = (await store.TryClaimAsync(run.Id, 3, Environment.ProcessId, process.StartTime.ToUniversalTime(), Ct))!;
        var scheduler = new LaunchScheduler(services.GetRequiredService<IServiceScopeFactory>(), store);
        run = (await store.GetRunAsync(run.Id, Ct))!;
        await scheduler.ReconcileAsync(run with { ClaimedUtc = DateTime.UtcNow.AddMinutes(-1) }, services, Ct);
        Assert.Equal("uncertain", (await store.GetRunAsync(run.Id, Ct))!.Status);
        Assert.False(await store.StartAsync(claim, "late", null, root, Environment.ProcessId, process.StartTime.ToUniversalTime(), ct: Ct));
        await scheduler.ReconcileAsync((await store.GetRunAsync(run.Id, Ct))!, services, Ct);
        Assert.Equal("interrupted", (await store.GetRunAsync(run.Id, Ct))!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleNativeClaimCannotSealAStartedProcess(bool script)
    {
        await using var services = Services();
        var run = await Enqueue(services.GetRequiredService<LaunchService>(), script);
        using var process = Process.GetCurrentProcess();
        var claim = (await store.TryClaimAsync(run.Id, 3, Environment.ProcessId, process.StartTime.ToUniversalTime(), Ct))!;
        var stale = (await store.GetRunAsync(run.Id, Ct))! with { ClaimedUtc = DateTime.UtcNow.AddMinutes(-1) };
        Assert.True(await store.StartAsync(claim, "session", null, root, Environment.ProcessId, process.StartTime.ToUniversalTime(), ct: Ct));
        await new LaunchScheduler(services.GetRequiredService<IServiceScopeFactory>(), store).ReconcileAsync(stale, services, Ct);
        Assert.Equal("running", (await store.GetRunAsync(run.Id, Ct))!.Status);
        Assert.False(await store.CompleteUnstartedAsync(claim, "failed", ct: Ct));
    }

    [Fact]
    public async Task NativeLauncherExceptionReleasesItsUnstartedClaim()
    {
        await using var services = Services();
        var service = services.GetRequiredService<LaunchService>();
        var run = await Enqueue(service, false);
        var native = new Mock<IJobProcessLauncher>();
        native.Setup(x => x.Launch(root, It.IsAny<IReadOnlyList<string>>(), false)).Throws<IOException>();
        var dispatcher = new LaunchDispatcher(store, service, Mock.Of<ITerminalTabHostService>(MockBehavior.Strict),
            services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<IBoardStore>(), native.Object);
        Assert.Equal("failed", (await dispatcher.DispatchAsync(run.Id, Ct)).Status);
        Assert.Empty(await store.GetActiveRunsAsync(Ct));
    }

    public void Dispose() => Directory.Delete(root, true);
}
