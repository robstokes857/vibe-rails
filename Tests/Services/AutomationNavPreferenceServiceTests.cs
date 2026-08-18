using System.Text.Json;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Jobs;
using VibeRails.Services.PythonScripts;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services;

public sealed class AutomationNavPreferenceServiceTests : IDisposable
{
    private const string ProjectPath = @"C:\test\automation-nav-project";
    private readonly string _originalRootPath = ParserConfigs.GetRootPath();
    private readonly bool _originalIsInGit = ParserConfigs.GetIsInGit();

    public AutomationNavPreferenceServiceTests()
    {
        ParserConfigs.SetGitState(ProjectPath, isInGit: true);
    }

    public void Dispose()
    {
        ParserConfigs.SetGitState(_originalRootPath, _originalIsInGit);
    }

    [Fact]
    public async Task GetAsync_ReturnsCatalogInJobOrderWithDefaultsEnabled()
    {
        var service = NewService([Job(1, "Nightly sweep"), Job(2, "Lint fix")], (string?)null, out _);

        var response = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["job:1", "job:2"], response.Items.Select(item => item.Key));
        Assert.Equal(["Nightly sweep", "Lint fix"], response.Items.Select(item => item.Label));
        Assert.All(response.Items, item => Assert.True(item.Enabled));
        Assert.Equal([0, 1], response.Items.Select(item => item.Order));
    }

    [Fact]
    public async Task SaveAndGet_RoundTripsOrderAndVisibility()
    {
        string? persisted = null;
        var service = NewService(
            [Job(1, "First"), Job(2, "Second"), Job(3, "Third")],
            () => persisted,
            out var repository);
        CaptureWrites(repository, json => persisted = json);
        var defaults = await service.GetAsync(TestContext.Current.CancellationToken);

        var reordered = new List<AutomationNavPreferenceItem>
        {
            defaults.Items.Single(item => item.Key == "job:3") with { Order = 0 },
            defaults.Items.Single(item => item.Key == "job:1") with { Order = 1, Enabled = false },
            defaults.Items.Single(item => item.Key == "job:2") with { Order = 2 }
        };
        var saved = await service.SaveAsync(
            new UpdateAutomationNavPreferencesRequest(reordered),
            TestContext.Current.CancellationToken);
        var loaded = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(persisted);
        Assert.Equal(["job:3", "job:1", "job:2"], loaded.Items.Select(item => item.Key));
        Assert.False(loaded.Items.Single(item => item.Key == "job:1").Enabled);
        Assert.Equal(
            saved.Items.Select(item => (item.Key, item.Enabled, item.Order)),
            loaded.Items.Select(item => (item.Key, item.Enabled, item.Order)));
    }

    [Fact]
    public async Task SaveAsync_PreservesEntriesForJobsOutsideTheCurrentCatalog()
    {
        var foreignDocument = JsonSerializer.Serialize(
            new AutomationNavPreferenceDocument(
                AutomationNavPreferenceService.DocumentVersion,
                ["job:99", "job:1"],
                ["job:99"]),
            AppJsonSerializerContext.Default.AutomationNavPreferenceDocument);
        string? persisted = foreignDocument;
        var service = NewService([Job(1, "Mine")], () => persisted, out var repository);
        CaptureWrites(repository, json => persisted = json);

        var current = await service.GetAsync(TestContext.Current.CancellationToken);
        await service.SaveAsync(
            new UpdateAutomationNavPreferencesRequest([current.Items.Single() with { Enabled = false }]),
            TestContext.Current.CancellationToken);

        var document = JsonSerializer.Deserialize(
            persisted!,
            AppJsonSerializerContext.Default.AutomationNavPreferenceDocument);
        Assert.NotNull(document);
        Assert.Contains("job:99", document.Order);
        Assert.Contains("job:99", document.HiddenKeys);
        Assert.Contains("job:1", document.HiddenKeys);
    }

    [Fact]
    public async Task SaveAsync_RejectsUnknownMissingAndMismatchedItems()
    {
        var service = NewService([Job(1, "Only")], (string?)null, out _);
        var current = await service.GetAsync(TestContext.Current.CancellationToken);
        var item = current.Items.Single();

        await Assert.ThrowsAsync<AutomationNavPreferenceValidationException>(() => service.SaveAsync(
            new UpdateAutomationNavPreferencesRequest(null),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AutomationNavPreferenceValidationException>(() => service.SaveAsync(
            new UpdateAutomationNavPreferencesRequest([]),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AutomationNavPreferenceValidationException>(() => service.SaveAsync(
            new UpdateAutomationNavPreferencesRequest([item with { Key = "job:42", JobId = 42 }]),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AutomationNavPreferenceValidationException>(() => service.SaveAsync(
            new UpdateAutomationNavPreferencesRequest([item with { Label = "Renamed elsewhere" }]),
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AutomationNavPreferenceValidationException>(() => service.SaveAsync(
            new UpdateAutomationNavPreferencesRequest([item, item]),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResetAsync_ClearsCurrentProjectKeysButKeepsForeignOnes()
    {
        var storedDocument = JsonSerializer.Serialize(
            new AutomationNavPreferenceDocument(
                AutomationNavPreferenceService.DocumentVersion,
                ["job:2", "job:1", "job:99"],
                ["job:1", "job:99"]),
            AppJsonSerializerContext.Default.AutomationNavPreferenceDocument);
        string? persisted = storedDocument;
        var service = NewService(
            [Job(1, "First"), Job(2, "Second")],
            () => persisted,
            out var repository);
        CaptureWrites(repository, json => persisted = json);

        var response = await service.ResetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["job:1", "job:2"], response.Items.Select(item => item.Key));
        Assert.All(response.Items, item => Assert.True(item.Enabled));
        var document = JsonSerializer.Deserialize(
            persisted!,
            AppJsonSerializerContext.Default.AutomationNavPreferenceDocument);
        Assert.NotNull(document);
        Assert.Equal(["job:99"], document.Order);
        Assert.Equal(["job:99"], document.HiddenKeys);
    }

    [Fact]
    public async Task GetAsync_IgnoresCorruptDocumentsAndAppendsNewJobsAfterSavedOrder()
    {
        var corruptService = NewService([Job(1, "Solo")], "{not json", out _);
        var corrupt = await corruptService.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["job:1"], corrupt.Items.Select(item => item.Key));

        var savedDocument = JsonSerializer.Serialize(
            new AutomationNavPreferenceDocument(
                AutomationNavPreferenceService.DocumentVersion,
                ["job:2", "job:1"],
                []),
            AppJsonSerializerContext.Default.AutomationNavPreferenceDocument);
        var service = NewService(
            [Job(1, "First"), Job(2, "Second"), Job(3, "Newcomer")],
            savedDocument,
            out _);

        var response = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["job:2", "job:1", "job:3"], response.Items.Select(item => item.Key));
        Assert.Equal([0, 1, 2], response.Items.Select(item => item.Order));
    }

    [Fact]
    public async Task GetAsync_AppendsPythonScriptsAfterJobsAndRoundTripsTheirVisibility()
    {
        string? persisted = null;
        var service = NewService(
            [Job(1, "Job one")],
            () => persisted,
            out var repository,
            scripts: [Script("Backup.py"), Script("tidy.py", status: "unapproved")]);
        CaptureWrites(repository, json => persisted = json);

        var defaults = await service.GetAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["job:1", "script:Backup.py", "script:tidy.py"], defaults.Items.Select(item => item.Key));
        Assert.Equal(["automation", "script", "script"], defaults.Items.Select(item => item.Kind));
        Assert.Equal(["Job one", "Backup.py", "tidy.py"], defaults.Items.Select(item => item.Label));

        var reordered = new List<AutomationNavPreferenceItem>
        {
            defaults.Items.Single(item => item.Key == "script:Backup.py") with { Order = 0 },
            defaults.Items.Single(item => item.Key == "job:1") with { Order = 1 },
            defaults.Items.Single(item => item.Key == "script:tidy.py") with { Order = 2, Enabled = false }
        };
        await service.SaveAsync(
            new UpdateAutomationNavPreferencesRequest(reordered),
            TestContext.Current.CancellationToken);
        var loaded = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["script:Backup.py", "job:1", "script:tidy.py"], loaded.Items.Select(item => item.Key));
        Assert.False(loaded.Items.Single(item => item.Key == "script:tidy.py").Enabled);
    }

    private static AutomationNavPreferenceService NewService(
        List<JobResponse> jobs,
        string? document,
        out Mock<IRepository> repository,
        List<PythonScriptInfo>? scripts = null) =>
        NewService(jobs, () => document, out repository, scripts);

    private static AutomationNavPreferenceService NewService(
        List<JobResponse> jobs,
        Func<string?> document,
        out Mock<IRepository> repository,
        List<PythonScriptInfo>? scripts = null)
    {
        repository = new Mock<IRepository>(MockBehavior.Loose);
        repository
            .Setup(item => item.GetGlobalCacheValueAsync(
                AutomationNavPreferenceService.CacheKey,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(document);
        var jobService = new Mock<IJobService>(MockBehavior.Loose);
        jobService
            .Setup(item => item.GetJobsAsync(ProjectPath, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobListResponse(jobs));
        var pythonScripts = new Mock<IPythonScriptService>(MockBehavior.Loose);
        pythonScripts
            .Setup(item => item.GetStatusAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PythonScriptListResponse(
                PinConfigured: false,
                ScriptsDirectory: @"C:\test\scripts",
                Scripts: scripts ?? []));
        return new AutomationNavPreferenceService(
            repository.Object, jobService.Object, pythonScripts.Object);
    }

    private static PythonScriptInfo Script(string name, string status = "approved") =>
        new(name, status, ApprovedUtc: null, ModifiedUtc: null, SizeBytes: 12);

    private static void CaptureWrites(Mock<IRepository> repository, Action<string> onWrite) =>
        repository
            .Setup(item => item.SetGlobalCacheValueAsync(
                AutomationNavPreferenceService.CacheKey,
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback((string _, string json, CancellationToken _) => onWrite(json))
            .Returns(Task.CompletedTask);

    private static JobResponse Job(long id, string name) => new(
        id,
        name,
        ProjectPath,
        LLM.Claude,
        EnvironmentId: 5,
        EnvironmentName: "Worker",
        Prompt: "do things",
        TimeoutMinutes: 30,
        Enabled: true,
        CreatedUtc: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        UpdatedUtc: new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
        DeletedUtc: null,
        Triggers: []);
}
