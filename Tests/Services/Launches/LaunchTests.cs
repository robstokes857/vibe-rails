using VibeRails.Services;
using VibeRails;
using VibeRails.DB;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services.Board;
using VibeRails.Services.Jobs;
using VibeRails.Services.Launches;
using VibeRails.Services.Mcp;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Launches;

public sealed class LaunchTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "viberails-launch-tests-" + Guid.NewGuid().ToString("N"));
    private readonly LaunchStore store;
    private readonly LaunchService service;
    private string Project => LaunchService.ProjectKey(root);
    private CancellationToken Ct => TestContext.Current.CancellationToken;
    public LaunchTests()
    {
        Directory.CreateDirectory(root);
        store = new LaunchStore($"Data Source={Path.Combine(root, "launches.db")};Pooling=False");
        service = new LaunchService(store, Mock.Of<IAutomationScriptService>());
    }
    private Task<SavedPreset> Preset(string? prompt = "Do the task", string? id = null, long revision = 0) =>
        service.SavePresetAsync(id, new("Test", "Description", "codex", InitialPrompt: prompt, ExpectedRevision: revision), Ct);
    private Task<LaunchRun> Enqueue(string id, string occurrence = "once", string trigger = "manual") =>
        service.EnqueueAsync("preset", id, new(root, root, trigger, occurrence), Ct);

    [Fact]
    public async Task BlankPromptClassifiesAndRejectsAutomaticAdmission()
    {
        var p = (await Preset("  ")).Preset;
        Assert.Equal("quick-launch", p.Kind);
        var catalog = Assert.Single((await service.CatalogAsync(root, Ct)).Items);
        Assert.True(catalog.CanLaunch);
        Assert.False(catalog.CanRunAutomatically);
        await Assert.ThrowsAsync<LaunchValidationException>(() => Enqueue(p.Id, trigger: "schedule"));
        Assert.Equal("queued", (await Enqueue(p.Id)).Status);
    }

    [Fact]
    public async Task LostAcknowledgmentFindsOriginalEvenAfterPromptCleared()
    {
        var p = (await Preset()).Preset;
        var original = await Enqueue(p.Id, trigger: "board-lane");
        await Preset("", p.Id, p.Revision);
        var replay = await Enqueue(p.Id, trigger: "board-lane");
        Assert.Equal(original.Id, replay.Id);
        Assert.Equal("agent", replay.Snapshot.Preset!.Kind);
        await service.ValidateSnapshotAsync(replay.Snapshot, Ct);
        await Assert.ThrowsAsync<LaunchValidationException>(() => Enqueue(p.Id, "new", "schedule"));
    }

    [Fact]
    public async Task RetryUsesOriginalWhileRunNowUsesLatest()
    {
        var p = (await Preset("first")).Preset;
        var run = await Enqueue(p.Id);
        var claim = (await store.TryClaimAsync(run.Id, 3, 11, DateTime.UtcNow, Ct))!;
        await store.CompleteAsync(claim, "failed", ct: Ct);
        await Preset("second", p.Id, p.Revision);
        var retry = await service.RetryAsync(root, run.Id, Ct);
        Assert.Equal(run.Id, retry.SourceRunId);
        Assert.Equal("first", retry.Snapshot.Preset!.InitialPrompt);
        Assert.Equal("second", (await Enqueue(p.Id, "new")).Snapshot.Preset!.InitialPrompt);
    }

    [Fact]
    public async Task RevisionRaceHasOneWinner()
    {
        var p = (await Preset()).Preset;
        async Task<bool> Edit(string prompt)
        {
            try { await Preset(prompt, p.Id, p.Revision); return true; }
            catch (LaunchConflictException ex) { Assert.Equal(2, ex.CurrentRevision); return false; }
        }
        var results = await Task.WhenAll(Task.Run(() => Edit("one"), Ct), Task.Run(() => Edit("two"), Ct));
        Assert.Single(results.Where(x => x));
    }

    [Fact]
    public async Task ClaimsAreAtomicAcrossConnectionsAndCapacityIsGlobal()
    {
        var p = (await Preset()).Preset;
        var run = await Enqueue(p.Id);
        var otherStore = new LaunchStore($"Data Source={Path.Combine(root, "launches.db")};Pooling=False");
        var claims = await Task.WhenAll(
            Task.Run(() => store.TryClaimAsync(run.Id, 1, 11, DateTime.UtcNow, Ct), Ct),
            Task.Run(() => otherStore.TryClaimAsync(run.Id, 1, 12, DateTime.UtcNow, Ct), Ct));
        var claim = Assert.Single(claims.Where(x => x is not null))!;
        var next = await Enqueue((await Preset()).Preset.Id, "next");
        Assert.Null(await store.TryClaimAsync(next.Id, 1, 12, DateTime.UtcNow, Ct));
        await store.CompleteAsync(claim, "succeeded", 0, ct: Ct);
        Assert.NotNull(await store.TryClaimAsync(next.Id, 1, 12, DateTime.UtcNow, Ct));
    }

    [Fact]
    public async Task UncertainClaimCannotStartOrReleaseCapacity()
    {
        var run = await Enqueue((await Preset()).Preset.Id);
        var claim = (await store.TryClaimAsync(run.Id, 1, 11, DateTime.UtcNow, Ct))!;
        await store.CompleteAsync(claim, "uncertain", ct: Ct);
        Assert.False((await store.GetRunAsync(run.Id, Ct))!.IsTerminal);
        Assert.False(await store.StartAsync(claim, "late", "prompt", root, 12, DateTime.UtcNow, ct: Ct));
        await Assert.ThrowsAsync<LaunchConflictException>(() => service.RetryAsync(root, run.Id, Ct));
        var next = await Enqueue((await Preset()).Preset.Id, "next");
        Assert.Null(await store.TryClaimAsync(next.Id, 1, 12, DateTime.UtcNow, Ct));
    }

    [Fact]
    public async Task CancellationIsProjectScopedAndRequiresOwnerAcknowledgment()
    {
        var run = await Enqueue((await Preset()).Preset.Id);
        var claim = (await store.TryClaimAsync(run.Id, 1, 11, DateTime.UtcNow, Ct))!;
        Assert.False(await store.CancelAsync(run.Id, Project + "-other", Ct));
        Assert.True(await store.StartAsync(claim, "session", "exact prompt", root, 11, DateTime.UtcNow, DateTime.UtcNow.AddMinutes(1), Ct));
        Assert.True(await store.CancelAsync(run.Id, Project, Ct));
        Assert.False((await store.GetRunAsync(run.Id, Ct))!.IsTerminal);
        Assert.False(await store.CompleteAsync(new(run.Id, "wrong-token"), "succeeded", ct: Ct));
        await store.CompleteAsync(claim, "failed", 1, ct: Ct);
        var finished = (await store.GetRunAsync(run.Id, Ct))!;
        Assert.Equal("cancelled", finished.Status);
        Assert.Equal("exact prompt", finished.ResolvedPrompt);
    }

    [Fact]
    public async Task SchedulerLeaseHasSingleOwnerAndExpires()
    {
        var now = DateTime.UtcNow;
        Assert.True(await store.TryLeaseAsync("scheduler", "a", now, TimeSpan.FromSeconds(10), Ct));
        Assert.False(await store.TryLeaseAsync("scheduler", "b", now, TimeSpan.FromSeconds(10), Ct));
        Assert.True(await store.TryLeaseAsync("scheduler", "a", now.AddSeconds(1), TimeSpan.FromSeconds(10), Ct));
        Assert.True(await store.TryLeaseAsync("scheduler", "b", now.AddSeconds(12), TimeSpan.FromSeconds(10), Ct));
    }

    [Fact]
    public async Task RetiredSyntaxRejectedOnSaveAndOldSnapshotExecution()
    {
        await Assert.ThrowsAsync<LaunchValidationException>(() => Preset("{{ STEP : old}}"));
        var p = (await Preset()).Preset;
        var invalid = new LaunchSnapshot(new(Project, root, "manual", "legacy"), p with { InitialPrompt = "{{step:old}}" });
        await Assert.ThrowsAsync<LaunchValidationException>(() => service.ValidateSnapshotAsync(invalid, Ct));
        await Assert.ThrowsAsync<LaunchValidationException>(() => store.EnqueueAsync(invalid, ct: Ct));
    }

    [Fact]
    public async Task ScriptApprovalIsScopedToProjectAndContent()
    {
        await store.ApproveScriptAsync(Project, "action", "hash-one", Ct);
        Assert.True(await store.IsScriptApprovedAsync(Project, "action", "hash-one", Ct));
        Assert.False(await store.IsScriptApprovedAsync(Project, "action", "hash-two", Ct));
        Assert.False(await store.IsScriptApprovedAsync(Project + "other", "action", "hash-one", Ct));
    }

    [Fact]
    public async Task AttachmentsRetainSnapshotOwnershipAfterRemovalAndRetirement()
    {
        var p = (await Preset()).Preset;
        var other = (await Preset()).Preset;
        var attachment = await new LaunchAttachments(store).AddAsync(p.Id, new("notes.txt", "data:text/plain;base64,aGVsbG8="), Ct);
        var run = await Enqueue(p.Id);
        Assert.Null(await store.ReadAttachmentAsync(other.Id, attachment.Id, Ct));
        await store.RemoveAttachmentAsync(p.Id, attachment.Id, Ct);
        Assert.Null(await store.ReadAttachmentAsync(p.Id, attachment.Id, Ct));
        await service.RetireAsync("preset", p.Id, p.Revision, Ct);
        Assert.Equal("hello", System.Text.Encoding.UTF8.GetString((await store.ReadRunAttachmentAsync(Project, run.Id, attachment.Id, Ct))!.Content));
        Assert.Null(await store.ReadRunAttachmentAsync(Project + "other", run.Id, attachment.Id, Ct));
        await Assert.ThrowsAsync<LaunchValidationException>(() => new LaunchAttachments(store).AddAsync(p.Id, new("new.txt", "data:text/plain;base64,aA=="), Ct));
    }

    [Fact]
    public async Task NativeAotMetadataCoversMcpAndHidesClaimSecrets()
    {
        var resolver = new Mock<IBoardProjectResolver>();
        resolver.Setup(r => r.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(root);
        var tool = new LaunchTool(service, store, resolver.Object);
        var p = await tool.SaveLaunchPreset(new("MCP", "", "codex", InitialPrompt: "task"), ct: Ct);
        var run = await tool.RunLaunch(p.Preset.Id, ct: Ct);
        Assert.Single((await tool.ListLaunchRuns(ct: Ct)).Runs);
        var claimed = run with { ClaimToken = "never-expose" };
        var json = JsonSerializer.Serialize(claimed, AppJsonSerializerContext.Default.LaunchRun);
        Assert.DoesNotContain("never-expose", json);
        Assert.NotNull(McpToolRegistration.SerializerOptions.GetTypeInfo(typeof(SavePresetRequest)));
        Assert.NotNull(McpToolRegistration.SerializerOptions.GetTypeInfo(typeof(LaunchAttachmentContent)));
    }

    [Fact]
    public void SingleActionsAndProviderOptionsAreValidated()
    {
        Assert.Throws<LaunchValidationException>(() => LaunchService.ValidateAction(new("id", "bad", "", "script", new("a.py", JobScriptRuntime.Python, []), new("working-tree"), true, 0, default, default)));
        var p = new LaunchPreset("id", "Test", "", LLM.Claude,
            new(new(Model: "claude-sonnet-4-6", Effort: "high", Yolo: true), new(FastMode: true, NoSessionPersistence: true, SystemPrompt: "literal $()", Bare: true, Debug: true)), [], "work", true, 1, default, default);
        var args = LaunchOptionBuilder.Build(p);
        Assert.Contains("--settings", args);
        Assert.Contains("{\"fastMode\":true}", args);
        Assert.Contains("literal $()", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.Contains("--bare", args);
        Assert.Contains("--debug", args);
        Assert.Throws<LaunchValidationException>(() => LaunchOptionBuilder.Build(p with { Cli = LLM.Codex }));
    }

    [Fact]
    public void SuccessfulExitDoesNotPassResultOrReviewGate()
    {
        var entry = new BoardLaunchEvent("event", Project, "card", "column", "board", "id", "preset", "review", 0, DateTime.UtcNow, true, "run", "succeeded", null, false, null);
        Assert.False(BoardLaunchWorkflow.ReleasesNext(entry));
        Assert.True(BoardLaunchWorkflow.ReleasesNext(entry with { Verdict = "passed" }));
        Assert.False(BoardLaunchWorkflow.ReleasesNext(entry with { Verdict = "passed", ProcessStatus = "running" }));
        Assert.True(BoardLaunchWorkflow.ReleasesNext(entry with { SkipRequested = true }));
    }

    [Fact]
    public async Task ScriptSaveNormalizesPathsAndRequiresIndependentProjectContentApproval()
    {
        var resolver = new Mock<IJobExecutableResolver>();
        resolver.Setup(x => x.Resolve(JobScriptRuntime.Python)).Returns(new JobExecutable("python", []));
        var launch = new LaunchService(store, new AutomationScriptService(resolver.Object));
        var path = Path.Combine(root, "check.py");
        await File.WriteAllTextAsync(path, "print('one')", Ct);
        var request = new SaveActionRequest("Check", "", "script", new(path, JobScriptRuntime.Python, []));
        var action = await launch.SaveActionAsync(root, null, request, Ct);
        Assert.Equal("check.py", action.Script!.Path);
        var run = await launch.EnqueueAsync("action", action.Id, new(root, root, "manual", "first"), Ct);
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(other);
        await File.WriteAllTextAsync(Path.Combine(other, "check.py"), "print('one')", Ct);
        await Assert.ThrowsAsync<LaunchValidationException>(() => launch.EnqueueAsync("action", action.Id, new(other, other, "manual", "other"), Ct));
        await launch.SaveActionAsync(other, action.Id, request with { Script = action.Script, ExpectedRevision = action.Revision }, Ct);
        Assert.NotNull(await launch.EnqueueAsync("action", action.Id, new(other, other, "manual", "approved"), Ct));
        await File.WriteAllTextAsync(path, "print('changed')", Ct);
        await Assert.ThrowsAsync<LaunchValidationException>(() => launch.ValidateSnapshotAsync(run.Snapshot, Ct));
    }

    [Fact]
    public async Task McpAttachmentReadsAreBoundedAndRetainedRunReadsStayProjectScoped()
    {
        var p = (await Preset()).Preset;
        var resolver = new Mock<IBoardProjectResolver>();
        resolver.Setup(x => x.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(root);
        var tool = new LaunchTool(service, store, resolver.Object);
        var file = await tool.AddLaunchAttachment(p.Id, new("context.txt", "data:text/plain;base64,aGVsbG8gd29ybGQ="), Ct);
        var run = await Enqueue(p.Id);
        var read = await tool.ReadLaunchAttachment(p.Id, file.Id, 6, 5, Ct);
        Assert.EndsWith("world", Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(Assert.Single(read.Content)).Text);
        Assert.True((await tool.ReadLaunchAttachment("wrong", file.Id, ct: Ct)).IsError);
        await tool.RemoveLaunchAttachment(p.Id, file.Id, Ct);
        Assert.False((await tool.ReadLaunchRunAttachment(run.Id, file.Id, ct: Ct)).IsError);
        resolver.Setup(x => x.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(root + "-other");
        Assert.True((await tool.ReadLaunchRunAttachment(run.Id, file.Id, ct: Ct)).IsError);
    }

    [Fact]
    public async Task OpenProjectPresenceExpiresWithoutReadingLegacyJobs()
    {
        var now = DateTime.UtcNow;
        await store.TryLeaseAsync("project:" + Project, "root", now, TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(Project, Assert.Single(await store.GetOpenProjectRootsAsync(now, Ct)));
        Assert.Empty(await store.GetOpenProjectRootsAsync(now.AddSeconds(31), Ct));
    }

    public void Dispose() => Directory.Delete(root, true);
}
