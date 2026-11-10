using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services;
using VibeRails.Services.Board;
using VibeRails.Services.Jobs;
using VibeRails.Services.Launches;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Launches;

public sealed class LaunchProjectScopeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "launch-scope-" + Guid.NewGuid().ToString("N"));
    private readonly LaunchStore store;
    private readonly LaunchService service;
    private readonly string project;
    private readonly string other;
    private string Connection => $"Data Source={Path.Combine(root, "launches.db")};Pooling=False";
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public LaunchProjectScopeTests()
    {
        project = Directory.CreateDirectory(Path.Combine(root, "One")).FullName;
        other = Directory.CreateDirectory(Path.Combine(root, "Two")).FullName;
        store = new(Connection);
        service = new(store, Mock.Of<IAutomationScriptService>());
    }

    private async Task<LaunchPreset> Save(string[]? paths = null, LaunchPreset? current = null) =>
        (await service.SavePresetAsync(current?.Id, new("Agent", "", "codex", InitialPrompt: "Work",
            ExpectedRevision: current?.Revision ?? 0, ProjectPaths: paths), Ct)).Preset;

    [Fact]
    public async Task ScopeIsAtomicNormalizedRetainedAcrossRestartAndOldWriterEdits()
    {
        var global = await Save();
        Assert.Empty(global.ProjectPaths!);
        var p = await Save([project, project + Path.DirectorySeparatorChar, Path.Combine(other, "..", "Two")], global);
        Assert.Equal(2, p.ProjectPaths!.Length);
        p = await Save(current: p);
        Assert.Equal(2, p.ProjectPaths!.Length);
        await Assert.ThrowsAsync<LaunchConflictException>(() => Save([], global));
        Assert.Equal(p.ProjectPaths, (await store.GetPresetAsync(p.Id, Ct))!.ProjectPaths);

        // Simulate a shipped writer that knows only the old JSON contract and never touches the mapping table.
        using (var db = new SqliteConnection(Connection))
        {
            await db.OpenAsync(Ct);
            using var command = db.CreateCommand();
            command.CommandText = "UPDATE LaunchPresets SET Payload=json_remove(Payload,'$.projectPaths') WHERE Id=$id;";
            command.Parameters.AddWithValue("$id", p.Id);
            await command.ExecuteNonQueryAsync(Ct);
        }
        var reopened = new LaunchStore(Connection);
        Assert.Equal(p.ProjectPaths, (await reopened.GetPresetAsync(p.Id, Ct))!.ProjectPaths);
        Directory.Delete(other);
        Assert.Equal(2, (await reopened.GetPresetAsync(p.Id, Ct))!.ProjectPaths!.Length);
        Assert.Empty((await Save([], p)).ProjectPaths!);
    }

    [Theory]
    [InlineData("manual")]
    [InlineData("board")]
    [InlineData("board-lane")]
    [InlineData("schedule")]
    [InlineData("before-commit")]
    [InlineData("after-commit")]
    public async Task EveryAdmissionUsesScopeAndStoreRechecksStaleSnapshots(string trigger)
    {
        var p = await Save([project]);
        Assert.Single((await service.CatalogAsync(project, Ct)).Items);
        Assert.Empty((await service.CatalogAsync(other, Ct)).Items);
        var elsewhere = Assert.Single((await service.CatalogAsync(other, Ct, includeAll: true)).Items);
        Assert.False(elsewhere.InProject);
        Assert.False(elsewhere.CanLaunch);
        Assert.Contains("not available in this project", elsewhere.IneligibleReason);
        await Assert.ThrowsAsync<LaunchValidationException>(() => service.EnqueueAsync("preset", p.Id, new(other, other, trigger, "blocked"), Ct));
        var admitted = await service.EnqueueAsync("preset", p.Id, new(project, project, trigger, "admitted"), Ct);
        await Save([other], p);
        await service.ValidateSnapshotAsync(admitted.Snapshot, Ct);
        Assert.Equal(admitted.Id, (await service.EnqueueAsync("preset", p.Id, new(project, project, trigger, "admitted"), Ct)).Id);
        // A revision-matched forged snapshot cannot bypass scope at the transaction boundary.
        var latest = (await store.GetPresetAsync(p.Id, Ct))!;
        await Assert.ThrowsAsync<LaunchValidationException>(() => store.EnqueueAsync(admitted.Snapshot with
        {
            Preset = latest, Context = admitted.Snapshot.Context with { OccurrenceId = "fresh" }
        }, ct: Ct));
        var claim = (await store.TryClaimAsync(admitted.Id, 3, 1, DateTime.UtcNow, Ct))!;
        await store.CompleteAsync(claim, "failed", ct: Ct);
        await Assert.ThrowsAsync<LaunchValidationException>(() => service.RetryAsync(project, admitted.Id, Ct));
        Assert.Single((await store.ListRunsAsync(LaunchProjectScope.Key(project), ct: Ct)).Runs);
    }

    [Fact]
    public async Task BindingsStaySavedButGitEventsCannotAdmitAfterNarrowing()
    {
        var p = await Save();
        var trigger = new JobTriggerRequest(JobTriggerKind.Commit);
        var binding = await service.SaveBindingAsync(project, null, new(p.Id, "preset", trigger), Ct);
        await Save([other], p);
        Assert.Equal(binding, Assert.Single(await store.ListBindingsAsync(ct: Ct)));
        Assert.Equal(0, await new LaunchEventService(store, service).EnqueueAsync(project, JobTriggerKind.Commit, "commit", Ct));
        await Assert.ThrowsAsync<LaunchValidationException>(() => service.SaveBindingAsync(project, null, new(p.Id, "preset", trigger), Ct));
    }

    [Fact]
    public async Task SchedulerAndLaneTicksRetainUnavailableConfigurationWithoutAdmittingRuns()
    {
        var p = await Save();
        var binding = await service.SaveBindingAsync(project, null, new(p.Id, "preset",
            new(JobTriggerKind.Schedule, JobScheduleKind.Interval, IntervalMinutes: 5)), Ct);
        var due = DateTime.UtcNow.AddMinutes(-1);
        binding = await store.SaveBindingAsync(binding with { NextRunUtc = due }, binding.Revision, Ct);
        var boardPath = Path.Combine(root, "board.db");
        var board = new BoardStore($"Data Source={boardPath};Pooling=False", $"Data Source={Path.Combine(root, "state.db")};Pooling=False");
        await board.EnsureDefaultColumnsAsync(project, Ct);
        var lane = (await board.GetColumnsAsync(project, Ct))[0];
        await board.SaveLaunchLaneAsync(project, lane.Id, new([new(p.Id, "preset")], 0), Ct);
        var card = await board.CreateCardAsync(project, new(lane.Id, "Card", "", null, "medium", null, [], false), Ct);
        using (var db = new SqliteConnection($"Data Source={boardPath};Pooling=False"))
        {
            await db.OpenAsync(Ct);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "UPDATE BoardLaunchEvents SET DueUnixMs=1;";
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        await Save([other], p);
        var resolver = new Mock<IBoardProjectResolver>();
        resolver.Setup(x => x.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(project);
        var services = new ServiceCollection();
        services.AddSingleton<ILaunchStore>(store);
        services.AddSingleton(service);
        services.AddSingleton<IBoardStore>(board);
        services.AddSingleton(resolver.Object);
        services.AddSingleton(Mock.Of<ITerminalTabHostService>());
        services.AddSingleton(new BoardLaunchWorkflow(board, store, service, null!));
        services.AddScoped<LaunchDispatcher>();
        await using var provider = services.BuildServiceProvider();
        var scheduler = new LaunchScheduler(provider.GetRequiredService<IServiceScopeFactory>(), store);
        await scheduler.TickAsync(Ct);
        Assert.Empty((await store.ListRunsAsync(LaunchProjectScope.Key(project), ct: Ct)).Runs);
        Assert.Equal(due, Assert.Single(await store.ListBindingsAsync(ct: Ct)).NextRunUtc);
        Assert.Equal(p.Id, Assert.Single((await board.GetLaunchLaneAsync(project, lane.Id, Ct))!.Selections).DefinitionId);
        Assert.Null(Assert.Single(await board.GetLaunchEventsAsync(project, card.Id, Ct)).RunId);
    }

    [Fact]
    public async Task ScriptManagementFromElsewhereKeepsApprovalsSeparate()
    {
        var resolver = new Mock<IJobExecutableResolver>();
        resolver.Setup(x => x.Resolve(JobScriptRuntime.Python)).Returns(new JobExecutable("python", []));
        var scripts = new LaunchService(store, new AutomationScriptService(resolver.Object));
        await File.WriteAllTextAsync(Path.Combine(project, "run.py"), "print('ok')", Ct);
        var request = new SaveActionRequest("Script", "", "script", new("run.py", JobScriptRuntime.Python, []), ProjectPaths: [project]);
        var a = await scripts.SaveActionAsync(project, null, request, Ct);
        var run = await scripts.EnqueueAsync("action", a.Id, new(project, project, "manual", "one"), Ct);
        a = await scripts.SaveActionAsync(other, a.Id, request with { Script = a.Script, ExpectedRevision = a.Revision, ProjectPaths = [other] }, Ct);
        Assert.False(await store.IsScriptApprovedAsync(LaunchProjectScope.Key(other), a.Id, run.Snapshot.ApprovedScriptHash!, Ct));
        Assert.True(await store.IsScriptApprovedAsync(LaunchProjectScope.Key(project), a.Id, run.Snapshot.ApprovedScriptHash!, Ct));
        await scripts.ValidateSnapshotAsync(run.Snapshot, Ct);
        Assert.False(Assert.Single((await scripts.CatalogAsync(other, Ct)).Items).CanLaunch);
    }

    [Fact]
    public async Task AGlobalScriptWithNoLocalFileCanStillBeScopedWithoutApproval()
    {
        var resolver = new Mock<IJobExecutableResolver>();
        resolver.Setup(x => x.Resolve(JobScriptRuntime.Python)).Returns(new JobExecutable("python", []));
        var scripts = new LaunchService(store, new AutomationScriptService(resolver.Object));
        await File.WriteAllTextAsync(Path.Combine(project, "run.py"), "print('ok')", Ct);
        var a = await scripts.SaveActionAsync(project, null, new("Script", "", "script", new("run.py", JobScriptRuntime.Python, [])), Ct);
        var result = await scripts.SaveActionAsync(other, a.Id, new("Renamed", "", "script", a.Script,
            ExpectedRevision: a.Revision, ProjectPaths: [project]), Ct);
        Assert.Equal("Renamed", result.Name);
        Assert.Equal([LaunchProjectScope.Key(project)], result.ProjectPaths);
        Assert.Empty((await scripts.CatalogAsync(other, Ct)).Items);
        await Assert.ThrowsAsync<AutomationScriptValidationException>(() => scripts.SaveActionAsync(other, a.Id,
            new("Changed", "", "script", a.Script! with { Path = "different.py" }, ExpectedRevision: result.Revision), Ct));
    }

    [Fact]
    public async Task KnownProjectsIncludePersistedClosedRootsAndSavedDisplayNames()
    {
        var repository = new Repository($"Data Source={Path.Combine(root, "state.db")};Pooling=False");
        await repository.SetProjectCacheValueAsync(project, ProjectCacheKeys.GitRootPath, project, Ct);
        await repository.SetProjectCacheValueAsync(other, ProjectCacheKeys.GitRootPath, other, Ct);
        await repository.CreateSessionAsync("session", "codex", null, other, Environment.ProcessId);
        Assert.True(await repository.UpdateLatestProjectDisplayNameAsync(other, "Closed project", Ct));
        var known = await LaunchProjects.ListAsync(repository, project, Ct);
        Assert.Equal(2, known.Count);
        Assert.Equal("Closed project", Assert.Single(known, p => p.Id == LaunchProjectScope.Key(other)).Name);
        Directory.Delete(other);
        Assert.Equal(known, await LaunchProjects.ListAsync(repository, project, Ct));
    }

    [Fact]
    public async Task KnownProjectsUseStableIdentityAndNamesNeverGroupClones()
    {
        var repository = new Mock<IRepository>();
        repository.Setup(x => x.GetKnownProjectPathsAsync(It.IsAny<CancellationToken>())).ReturnsAsync([other, project + Path.DirectorySeparatorChar]);
        repository.Setup(x => x.GetProjectDisplayNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("Same name");
        var known = await LaunchProjects.ListAsync(repository.Object, project, Ct);
        Assert.Equal(2, known.Count);
        Assert.True(known[0].IsCurrent);
        Assert.NotEqual(known[0].Id, known[1].Id);
        var p = await Save([known[0].Id]);
        repository.Setup(x => x.GetProjectDisplayNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync("Renamed");
        Assert.Equal(known.Select(x => x.Id), (await LaunchProjects.ListAsync(repository.Object, project, Ct)).Select(x => x.Id));
        Assert.Empty((await service.CatalogAsync(other, Ct)).Items);
        var projects = new Mock<IBoardProjectResolver>();
        projects.Setup(x => x.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(other);
        var picker = await new LlmPickerPreferenceService(repository.Object, store, projects.Object).GetAsync(Ct);
        Assert.False(Assert.Single(picker.Items, i => i.Key.StartsWith("preset:")).InProject);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("bad\npath")]
    public async Task InvalidScopeNeverCreatesADefinition(string path)
    {
        await Assert.ThrowsAsync<LaunchValidationException>(() => Save([path]));
        Assert.Empty(await store.ListPresetsAsync(Ct));
    }

    public void Dispose() => Directory.Delete(root, true);
}
