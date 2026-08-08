using System.Text.Json;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services;
using Xunit;

namespace Tests.Services;

public sealed class LlmPickerPreferenceServiceTests
{
    [Fact]
    public async Task GetAsync_ReturnsCanonicalDefaultsAndHonorsEnvironmentHidden()
    {
        var environments = new List<LLM_Environment>
        {
            Environment(10, "Visible", LLM.Claude),
            Environment(11, "Quiet", LLM.Codex, hidden: true)
        };
        var repository = NewRepository(environments, (string?)null);
        var service = new LlmPickerPreferenceService(repository.Object);

        var response = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["base:claude", "base:codex", "base:glm-5.2", "base:opencode",
             "base:copilot", "base:antigravity", "base:shell"],
            response.Items.Where(item => item.Kind == "base").Select(item => item.Key));
        Assert.True(response.Items.Single(item => item.Key == "env:10:claude").Enabled);
        Assert.False(response.Items.Single(item => item.Key == "env:11:codex").Enabled);
        Assert.Equal(
            Enumerable.Range(0, 7),
            response.Items.Where(item => item.Kind == "base").Select(item => item.Order));
    }

    [Fact]
    public async Task SaveAndGet_RoundTripsVisibilityAndBothGroupOrders()
    {
        string? persistedDocument = null;
        var environments = new List<LLM_Environment>
        {
            Environment(20, "First", LLM.Claude),
            Environment(21, "Second", LLM.Codex)
        };
        var repository = NewRepository(environments, () => persistedDocument);
        repository.Setup(item => item.SaveLlmPickerStateAsync(
                LlmPickerPreferenceService.CacheKey,
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<int, bool>>(),
                It.IsAny<CancellationToken>()))
            .Callback((string _, string? json, IReadOnlyDictionary<int, bool> hidden, CancellationToken _) =>
            {
                persistedDocument = json;
                foreach (var environment in environments)
                {
                    if (hidden.TryGetValue(environment.Id, out var value)) environment.Hidden = value;
                }
            })
            .Returns(Task.CompletedTask);
        var service = new LlmPickerPreferenceService(repository.Object);
        var defaults = await service.GetAsync(TestContext.Current.CancellationToken);
        var reordered = Reorder(
            defaults.Items,
            baseKeys: defaults.Items.Where(item => item.Kind == "base").Select(item => item.Key).Reverse(),
            environmentKeys: ["env:21:codex", "env:20:claude"],
            disabledKeys: new HashSet<string>(["base:codex", "env:20:claude"]));

        var saved = await service.SaveAsync(
            new UpdateLlmPickerPreferencesRequest(reordered),
            TestContext.Current.CancellationToken);
        var loaded = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(persistedDocument);
        Assert.Equal(
            saved.Items.Select(item => (item.Key, item.Enabled, item.Order)),
            loaded.Items.Select(item => (item.Key, item.Enabled, item.Order)));
        Assert.Equal("base:shell", loaded.Items.First(item => item.Kind == "base").Key);
        Assert.Equal("env:21:codex", loaded.Items.First(item => item.Kind == "environment").Key);
        Assert.False(loaded.Items.Single(item => item.Key == "base:codex").Enabled);
        Assert.True(environments.Single(item => item.Id == 20).Hidden);
        Assert.False(environments.Single(item => item.Id == 21).Hidden);
    }

    [Fact]
    public async Task GetAsync_AppendsNewCatalogItemsAndIgnoresDeletedEnvironmentKeys()
    {
        var document = new LlmPickerPreferenceDocument(
            1,
            ["base:codex", "base:claude"],
            ["env:999:codex", "env:30:claude"],
            ["base:codex"]);
        var json = JsonSerializer.Serialize(
            document,
            AppJsonSerializerContext.Default.LlmPickerPreferenceDocument);
        var environments = new List<LLM_Environment>
        {
            Environment(30, "Existing", LLM.Claude),
            Environment(31, "New", LLM.OpenCode)
        };
        var service = new LlmPickerPreferenceService(NewRepository(environments, json).Object);

        var response = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal("base:codex", response.Items[0].Key);
        Assert.False(response.Items[0].Enabled);
        Assert.Equal("base:claude", response.Items[1].Key);
        Assert.Equal("base:glm-5.2", response.Items[2].Key);
        Assert.DoesNotContain(response.Items, item => item.Key.Contains("999", StringComparison.Ordinal));
        Assert.Equal(
            ["env:30:claude", "env:31:opencode"],
            response.Items.Where(item => item.Kind == "environment").Select(item => item.Key));
    }

    [Theory]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":1,\"baseOrder\":[null],\"environmentOrder\":[],\"disabledBaseKeys\":[]}")]
    public async Task GetAsync_IncompleteButValidDocumentFallsBackToDefaults(string json)
    {
        var service = new LlmPickerPreferenceService(NewRepository([], json).Object);

        var response = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal("base:claude", response.Items[0].Key);
        Assert.All(response.Items, item => Assert.True(item.Enabled));
    }

    [Fact]
    public async Task SaveAsync_RejectsDuplicateKeysPositionsUnknownItemsAndCrossGroupMoves()
    {
        var environments = new List<LLM_Environment> { Environment(40, "Work", LLM.Codex) };
        var repository = NewRepository(environments, (string?)null);
        var service = new LlmPickerPreferenceService(repository.Object);
        var defaults = (await service.GetAsync(TestContext.Current.CancellationToken)).Items;

        var duplicateKey = defaults.Select(item => item with { }).ToList();
        duplicateKey[1] = duplicateKey[1] with { Key = duplicateKey[0].Key };
        await Assert.ThrowsAsync<LlmPickerPreferenceValidationException>(() =>
            service.SaveAsync(new(duplicateKey), TestContext.Current.CancellationToken));

        var duplicatePosition = defaults.Select(item => item with { }).ToList();
        duplicatePosition[1] = duplicatePosition[1] with { Order = duplicatePosition[0].Order };
        await Assert.ThrowsAsync<LlmPickerPreferenceValidationException>(() =>
            service.SaveAsync(new(duplicatePosition), TestContext.Current.CancellationToken));

        var unknown = defaults.Select(item => item with { }).ToList();
        unknown[0] = unknown[0] with { Key = "base:invented", Cli = "invented", Label = "Invented" };
        await Assert.ThrowsAsync<LlmPickerPreferenceValidationException>(() =>
            service.SaveAsync(new(unknown), TestContext.Current.CancellationToken));

        var crossGroup = defaults.Select(item => item with { }).ToList();
        crossGroup[0] = crossGroup[0] with { Group = "Custom Environments" };
        await Assert.ThrowsAsync<LlmPickerPreferenceValidationException>(() =>
            service.SaveAsync(new(crossGroup), TestContext.Current.CancellationToken));

        repository.Verify(item => item.SaveLlmPickerStateAsync(
            It.IsAny<string>(),
            It.IsAny<string?>(),
            It.IsAny<IReadOnlyDictionary<int, bool>>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResetAsync_RemovesDocumentAndMakesAllEnvironmentsVisible()
    {
        var environments = new List<LLM_Environment>
        {
            Environment(50, "One", LLM.Claude, hidden: true),
            Environment(51, "Two", LLM.Codex, hidden: true)
        };
        var repository = NewRepository(environments, "{\"version\":1}");
        repository.Setup(item => item.SaveLlmPickerStateAsync(
                LlmPickerPreferenceService.CacheKey,
                null,
                It.IsAny<IReadOnlyDictionary<int, bool>>(),
                It.IsAny<CancellationToken>()))
            .Callback((string _, string? _, IReadOnlyDictionary<int, bool> hidden, CancellationToken _) =>
            {
                foreach (var environment in environments) environment.Hidden = hidden[environment.Id];
            })
            .Returns(Task.CompletedTask);
        var service = new LlmPickerPreferenceService(repository.Object);

        var response = await service.ResetAsync(TestContext.Current.CancellationToken);

        Assert.All(response.Items, item => Assert.True(item.Enabled));
        Assert.All(environments, environment => Assert.False(environment.Hidden));
        repository.Verify(item => item.SaveLlmPickerStateAsync(
            LlmPickerPreferenceService.CacheKey,
            null,
            It.Is<IReadOnlyDictionary<int, bool>>(values => values.Values.All(hidden => !hidden)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetAsync_ExcludesAutomationWorkersRegardlessOfHidden()
    {
        var environments = new List<LLM_Environment>
        {
            Environment(60, "Regular", LLM.Claude),
            Environment(61, "Worker", LLM.Codex, automationWorker: true),
            Environment(62, "HiddenWorker", LLM.Codex, hidden: true, automationWorker: true)
        };
        var service = new LlmPickerPreferenceService(
            NewRepository(environments, (string?)null).Object);

        var response = await service.GetAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["env:60:claude"],
            response.Items.Where(item => item.Kind == "environment").Select(item => item.Key));
    }

    [Fact]
    public async Task ResetAsync_NeverTouchesAutomationWorkerVisibility()
    {
        var environments = new List<LLM_Environment>
        {
            Environment(70, "Regular", LLM.Claude, hidden: true),
            Environment(71, "Worker", LLM.Codex, hidden: true, automationWorker: true)
        };
        var repository = NewRepository(environments, "{\"version\":1}");
        var service = new LlmPickerPreferenceService(repository.Object);

        await service.ResetAsync(TestContext.Current.CancellationToken);

        Assert.False(environments.Single(item => item.Id == 70).Hidden);
        Assert.True(environments.Single(item => item.Id == 71).Hidden);
        repository.Verify(item => item.SaveLlmPickerStateAsync(
            LlmPickerPreferenceService.CacheKey,
            null,
            It.Is<IReadOnlyDictionary<int, bool>>(values =>
                values.ContainsKey(70) && !values.ContainsKey(71)),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<IRepository> NewRepository(
        List<LLM_Environment> environments,
        string? document)
        => NewRepository(environments, () => document);

    private static Mock<IRepository> NewRepository(
        List<LLM_Environment> environments,
        Func<string?> document)
    {
        var repository = new Mock<IRepository>();
        repository.Setup(item => item.GetCustomEnvironmentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(environments);
        repository.Setup(item => item.GetGlobalCacheValueAsync(
                LlmPickerPreferenceService.CacheKey,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(document);
        return repository;
    }

    private static LLM_Environment Environment(
        int id, string name, LLM llm, bool hidden = false, bool automationWorker = false) => new()
    {
        Id = id,
        CustomName = name,
        LLM = llm,
        Hidden = hidden,
        AutomationWorker = automationWorker
    };

    private static List<LlmPickerPreferenceItem> Reorder(
        IReadOnlyCollection<LlmPickerPreferenceItem> source,
        IEnumerable<string> baseKeys,
        IEnumerable<string> environmentKeys,
        IReadOnlySet<string> disabledKeys)
    {
        var byKey = source.ToDictionary(item => item.Key, StringComparer.Ordinal);
        var result = new List<LlmPickerPreferenceItem>();
        result.AddRange(baseKeys.Select((key, order) => byKey[key] with
        {
            Order = order,
            Enabled = !disabledKeys.Contains(key)
        }));
        result.AddRange(environmentKeys.Select((key, order) => byKey[key] with
        {
            Order = order,
            Enabled = !disabledKeys.Contains(key)
        }));
        return result;
    }
}
