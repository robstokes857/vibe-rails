using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services;
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
    public void NativeAgentPromptKeepsOverridesAndAddsCompletionGuidance()
    {
        var preset = new LaunchPreset("preset", "Agent", "", LLM.Codex, new(), [], "Saved {{git_branch}}",
            true, 1, DateTime.UtcNow, DateTime.UtcNow);
        var snapshot = new LaunchSnapshot(new(root, root, "manual", "run", NativeTerminal: true), preset);
        foreach (var context in new[]
        {
            snapshot.Context,
            snapshot.Context with { PromptOverride = "Override {{git_branch}}" },
            snapshot.Context with { ComposedPrompt = "Context and task {{git_branch}}", PromptOverride = "Ignored override" }
        })
        {
            var prompt = NativeLaunchRunner.ComposePrompt(snapshot with { Context = context });
            Assert.StartsWith("This is a standalone Agent run in a native terminal.", prompt);
            Assert.Contains("end_agent_session as your last tool call", prompt);
            Assert.Contains("Do not call it while work or an approval you need is still pending", prompt);
            Assert.EndsWith(context.ComposedPrompt ?? context.PromptOverride ?? preset.InitialPrompt, prompt);
        }
    }

    [Theory]
    [InlineData("manual", null, false, true)]
    [InlineData("manual", null, true, true)]
    [InlineData("schedule", null, true, false)]
    [InlineData("before-commit", null, true, false)]
    [InlineData("board", null, false, false)]
    [InlineData("board-lane", "card", false, false)]
    [InlineData("manual", "card", false, false)]
    public async Task TerminalPickerPresentationIsLimitedToManualNonBoardPresets(
        string trigger, string? card, bool requestedNative, bool interactive)
    {
        var service = new LaunchService(store, scripts.Object);
        var preset = (await service.SavePresetAsync(null, new("Agent", "", "codex", InitialPrompt: "Work"), Ct)).Preset;
        var run = await service.EnqueueAsync("preset", preset.Id,
            new(root, root, trigger, "picker", CardId: card, NativeTerminal: requestedNative, TerminalTab: true), Ct);
        var saved = (await store.GetRunAsync(run.Id, Ct))!;
        Assert.Equal(interactive, saved.Snapshot.IsInteractiveTerminal);
        Assert.Equal(interactive, saved.Snapshot.Context.TerminalTab);
        Assert.Equal(!interactive && card is null && trigger != "board", saved.Snapshot.Context.NativeTerminal);
    }

    [Theory]
    [InlineData("Work")]
    [InlineData(null)]
    public async Task TerminalPickerDispatchCreatesAnOrdinaryTabAndRetryKeepsItsPresentation(string? prompt)
    {
        await using var services = Services();
        var service = new LaunchService(store, scripts.Object);
        var preset = (await service.SavePresetAsync(null, new("Saved launch", "", "codex", InitialPrompt: prompt), Ct)).Preset;
        var run = await service.EnqueueAsync("preset", preset.Id,
            new(root, root, "manual", "picker", TerminalTab: true), Ct);
        var tabs = new Mock<ITerminalTabHostService>(MockBehavior.Strict);
        tabs.Setup(t => t.CreateTabAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TerminalTabStatusResponse("tab", DateTime.UtcNow, false));
        LaunchClaim? claim = null;
        tabs.Setup(t => t.StartSessionAsync("tab", It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (string _, StartTerminalRequest request, CancellationToken token) =>
            {
                Assert.Equal("Saved launch", request.Title);
                Assert.Equal(root, request.WorkingDirectory);
                claim = request.LaunchClaim!;
                await store.StartAsync(claim, "session", prompt, root, Environment.ProcessId, DateTime.UtcNow, ct: token);
                return new TerminalStatusResponse(true, "session", "codex", root);
            });
        var dispatcher = new LaunchDispatcher(store, service, tabs.Object,
            services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<IBoardStore>(),
            Mock.Of<IJobProcessLauncher>(MockBehavior.Strict));
        var started = await dispatcher.DispatchAsync(run.Id, Ct);
        Assert.Equal("running", started.Status);
        Assert.Equal("tab", started.TabId);
        Assert.Equal("session", started.SessionId);
        tabs.Verify(t => t.RegisterLaunchTab(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        await store.CompleteAsync(claim!, "failed", ct: Ct);
        var retry = await service.RetryAsync(root, run.Id, Ct);
        Assert.True(retry.Snapshot.IsInteractiveTerminal);
        Assert.False(retry.Snapshot.Context.NativeTerminal);
    }

    [Fact]
    public async Task StartingTheSameSavedLaunchTwiceFromThePickerOpensTwoTabs()
    {
        await using var services = Services();
        var service = new LaunchService(store, scripts.Object);
        var preset = (await service.SavePresetAsync(null, new("codex_yolo", "", "codex"), Ct)).Preset;
        var created = 0;
        var tabs = new Mock<ITerminalTabHostService>(MockBehavior.Strict);
        tabs.Setup(t => t.CreateTabAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new TerminalTabStatusResponse("tab-" + ++created, DateTime.UtcNow, false));
        tabs.Setup(t => t.StartSessionAsync(It.IsAny<string>(), It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (string tab, StartTerminalRequest request, CancellationToken token) =>
            {
                // The session stays open: nothing completes the first run before the second starts.
                await store.StartAsync(request.LaunchClaim!, "session-" + tab, null, root, Environment.ProcessId, DateTime.UtcNow, ct: token);
                return new TerminalStatusResponse(true, "session-" + tab, "codex", root);
            });
        var dispatcher = new LaunchDispatcher(store, service, tabs.Object,
            services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<IBoardStore>(),
            Mock.Of<IJobProcessLauncher>(MockBehavior.Strict));

        var first = await dispatcher.DispatchAsync((await service.EnqueueAsync("preset", preset.Id,
            new(root, root, "manual", "first", TerminalTab: true), Ct)).Id, Ct);
        var second = await dispatcher.DispatchAsync((await service.EnqueueAsync("preset", preset.Id,
            new(root, root, "manual", "second", TerminalTab: true), Ct)).Id, Ct);

        Assert.Equal(("running", "tab-1"), (first.Status, first.TabId));
        Assert.Equal(("running", "tab-2"), (second.Status, second.TabId));
    }

    private LaunchDispatcher PickerDispatcher(ServiceProvider services, LaunchService service, Mock<ITerminalTabHostService> tabs)
    {
        tabs.Setup(t => t.StartSessionAsync(It.IsAny<string>(), It.IsAny<StartTerminalRequest>(), It.IsAny<CancellationToken>()))
            .Returns(async (string tab, StartTerminalRequest request, CancellationToken token) =>
            {
                await store.StartAsync(request.LaunchClaim!, "session-" + tab, null, root, Environment.ProcessId, DateTime.UtcNow, ct: token);
                return new TerminalStatusResponse(true, "session-" + tab, "codex", root);
            });
        // A strict native launcher: nothing here may open a native window.
        return new LaunchDispatcher(store, service, tabs.Object, services.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<IBoardStore>(), Mock.Of<IJobProcessLauncher>(MockBehavior.Strict));
    }

    [Fact]
    public async Task ATabAskedForFromAPageOpensInTheBackendThatWasAsked()
    {
        await using var services = Services();
        var service = new LaunchService(store, scripts.Object);
        var preset = (await service.SavePresetAsync(null, new("codex_yolo", "", "codex"), Ct)).Preset;
        var tabs = new Mock<ITerminalTabHostService>(MockBehavior.Strict);
        tabs.Setup(t => t.CreateTabAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TerminalTabStatusResponse("tab-here", DateTime.UtcNow, false));
        var dispatcher = PickerDispatcher(services, service, tabs);

        // Two roots can be open on one project (a VS Code window and the browser). Left queued,
        // the run went to whichever ticked first: the tab opened in the other window and the
        // page that asked polled a tab its own backend never had. No root's scheduler takes it
        // while the backend that was asked can still claim it.
        var asked = await service.EnqueueAsync("preset", preset.Id, new(root, root, "manual", "picker", TerminalTab: true), Ct);
        Assert.False(LaunchScheduler.CanDispatchHere(asked, root, [asked.ProjectPath], asked.QueuedUtc));
        Assert.False(LaunchScheduler.CanDispatchHere(asked, root, [asked.ProjectPath],
            asked.QueuedUtc + LaunchScheduler.RequestedTabGrace - TimeSpan.FromMilliseconds(1)));

        var opened = await dispatcher.DispatchRequestedTabAsync(asked, Ct);
        Assert.Equal(("running", "tab-here", "session-tab-here"), (opened.Status, opened.TabId, opened.SessionId));
        // A repeated delivery of the same request finds the open run and starts nothing more.
        Assert.Equal("running", (await dispatcher.DispatchRequestedTabAsync(opened, Ct)).Status);
        tabs.Verify(t => t.CreateTabAsync(It.IsAny<CancellationToken>()), Times.Once);

        // A tab no backend claimed (queued by an older version's root, or retried from outside a
        // root) is not stranded: any root on the project opens it once the grace has passed.
        var unclaimed = await service.EnqueueAsync("preset", preset.Id, new(root, root, "manual", "unclaimed", TerminalTab: true), Ct);
        Assert.True(LaunchScheduler.CanDispatchHere(unclaimed, root, [unclaimed.ProjectPath],
            unclaimed.QueuedUtc + LaunchScheduler.RequestedTabGrace));

        // Every other run is still placed by the scheduler, at once: the request starts nothing.
        var native = await service.EnqueueAsync("preset", preset.Id, new(root, root, "manual", "native"), Ct);
        Assert.Equal("queued", (await dispatcher.DispatchRequestedTabAsync(native, Ct)).Status);
        Assert.True(LaunchScheduler.CanDispatchHere(native, root, [native.ProjectPath], native.QueuedUtc));
    }

    [Fact]
    public async Task ATabThatCannotBeCreatedAnswersTheRequestWithItsReason()
    {
        await using var services = Services();
        var service = new LaunchService(store, scripts.Object);
        var preset = (await service.SavePresetAsync(null, new("codex_yolo", "", "codex"), Ct)).Preset;
        var tabs = new Mock<ITerminalTabHostService>(MockBehavior.Strict);
        tabs.Setup(t => t.CreateTabAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("Tab limit reached."));
        var dispatcher = PickerDispatcher(services, service, tabs);

        var run = await dispatcher.DispatchRequestedTabAsync(
            await service.EnqueueAsync("preset", preset.Id, new(root, root, "manual", "picker", TerminalTab: true), Ct), Ct);

        // The page shows this in its message instead of waiting on a run that will never open.
        Assert.Equal(("failed", "The terminal could not be created."), (run.Status, run.Error));
    }

    [Fact]
    public async Task TerminalPickerTabsOpenBesideEachOtherAndLeaveAgentSlotsFree()
    {
        var service = new LaunchService(store, scripts.Object);
        var preset = (await service.SavePresetAsync(null, new("Agent", "", "codex", InitialPrompt: "Work"), Ct)).Preset;
        Task<LaunchRun> Tab(string occurrence) => service.EnqueueAsync("preset", preset.Id,
            new(root, root, "manual", occurrence, TerminalTab: true), Ct);
        // A picker tab's run stays active for as long as the user keeps the tab. The same saved
        // launch must still open again beside it, and past the limit on automatic agents.
        for (var i = 0; i <= LaunchDispatcher.Capacity; i++)
        {
            var tab = await store.TryClaimAsync((await Tab("tab-" + i)).Id, LaunchDispatcher.Capacity, 11, DateTime.UtcNow, Ct);
            Assert.NotNull(tab);
            Assert.True(await store.StartAsync(tab, "session-" + i, null, root, 11, DateTime.UtcNow, ct: Ct));
        }

        // Those open tabs hold neither a machine slot nor their definition, so other work still starts.
        var native = await service.EnqueueAsync("preset", preset.Id, new(root, root, "manual", "native"), Ct);
        Assert.NotNull(await store.TryClaimAsync(native.Id, 1, 11, DateTime.UtcNow, Ct));

        // Everything else keeps both limits: one active run per definition and project, and the slots.
        var repeat = await service.EnqueueAsync("preset", preset.Id, new(root, root, "manual", "native-again"), Ct);
        Assert.Null(await store.TryClaimAsync(repeat.Id, LaunchDispatcher.Capacity, 11, DateTime.UtcNow, Ct));
        var other = (await service.SavePresetAsync(null, new("Other", "", "codex", InitialPrompt: "Work"), Ct)).Preset;
        var blocked = await service.EnqueueAsync("preset", other.Id, new(root, root, "manual", "other"), Ct);
        Assert.Null(await store.TryClaimAsync(blocked.Id, 1, 11, DateTime.UtcNow, Ct));

        // A tab is not held up by that occupied slot or by the active run of its own definition.
        Assert.NotNull(await store.TryClaimAsync((await Tab("tab-late")).Id, 1, 11, DateTime.UtcNow, Ct));
    }

    [Theory]
    [InlineData("manual", null)]
    [InlineData("schedule", null)]
    [InlineData("before-commit", null)]
    [InlineData("board", null)]
    [InlineData("board-lane", "card")]
    [InlineData("manual", "card")]
    public async Task OnlyAnInteractiveTerminalTabIsExemptFromTheAgentLimits(string trigger, string? card)
    {
        var service = new LaunchService(store, scripts.Object);
        var holder = (await service.SavePresetAsync(null, new("Holder", "", "codex", InitialPrompt: "Work"), Ct)).Preset;
        var held = await service.EnqueueAsync("preset", holder.Id, new(root, root, "manual", "holder"), Ct);
        Assert.NotNull(await store.TryClaimAsync(held.Id, 1, 11, DateTime.UtcNow, Ct));

        var preset = (await service.SavePresetAsync(null, new("Agent", "", "codex", InitialPrompt: "Work"), Ct)).Preset;
        var run = await service.EnqueueAsync("preset", preset.Id,
            new(root, root, trigger, "candidate", CardId: card, TerminalTab: true), Ct);

        // The claim's SQL must agree with the snapshot's own definition of an interactive tab.
        var claim = await store.TryClaimAsync(run.Id, 1, 11, DateTime.UtcNow, Ct);
        Assert.Equal(run.Snapshot.IsInteractiveTerminal, claim is not null);
    }

    [Fact]
    public void NativeQuickLaunchDoesNotGetCompletionGuidance()
    {
        var preset = new LaunchPreset("preset", "Quick Launch", "", LLM.Codex, new(), [], null,
            true, 1, DateTime.UtcNow, DateTime.UtcNow);
        var snapshot = new LaunchSnapshot(new(root, root, "manual", "run", NativeTerminal: true), preset);
        Assert.Null(NativeLaunchRunner.ComposePrompt(snapshot));
        Assert.Equal("Ready to help", NativeLaunchRunner.ComposePrompt(snapshot with
        { Context = snapshot.Context with { ComposedPrompt = "Ready to help" } }));
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
