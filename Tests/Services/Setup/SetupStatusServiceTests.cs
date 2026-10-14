using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Setup;
using Xunit;

namespace Tests.Services.Setup;

public sealed class SetupStatusServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly string Launch = Path.Combine(Path.GetTempPath(), "vb-setup-status", "launch");

    private sealed class Machine
    {
        public readonly HashSet<string> Files = new(StringComparer.OrdinalIgnoreCase);
        public int Probes;

        /// <summary>Git, PowerShell 7 and one AI CLI: everything Windows setup requires.</summary>
        public Machine SetUp()
        {
            foreach (var exe in new[] { "git.exe", "pwsh.exe", "claude.exe" }) Files.Add(Path.Combine(Launch, exe));
            return this;
        }

        public SetupProbeEnvironment Environment()
        {
            Probes++;
            return new SetupProbeEnvironment(true, [Launch], [], [], Files.Contains, path => path, _ => null);
        }
    }

    private static SetupStatusService Service(FakeGlobalCache cache, Machine machine) =>
        new(cache) { ProbeEnvironment = machine.Environment, Platform = SetupPlatform.Windows, Clock = () => Now };

    [Fact]
    public async Task ACompleteSetupIsProbedOnceAndThenReadFromTheCache()
    {
        var cache = new FakeGlobalCache();
        var machine = new Machine().SetUp();

        var first = await Service(cache, machine).GetAsync();
        // Optional extras installed afterwards wait for the next install's re-check.
        machine.Files.Add(Path.Combine(Launch, "codex.exe"));
        var second = await Service(cache, machine).GetAsync();

        Assert.Equal(1, machine.Probes);
        Assert.Equal("ready", second.Tools.Single(tool => tool.Id == "claude").State);
        Assert.Equal("missing", second.Tools.Single(tool => tool.Id == "codex").State);
        Assert.Equal(Now, first.CheckedUtc);
        Assert.NotNull(cache.Values.GetValueOrDefault(SetupStatusService.StatusCacheKey));
    }

    [Fact]
    public async Task AnIncompleteSetupIsProbedOnEveryReadUntilItIsComplete()
    {
        var cache = new FakeGlobalCache();
        var machine = new Machine();
        Assert.Equal("missing", (await Service(cache, machine).GetAsync()).Tools.Single(tool => tool.Id == "pwsh").State);

        // Installed in a plain terminal, outside VibeRails: the next app load notices.
        machine.SetUp();
        var after = await Service(cache, machine).GetAsync();
        await Service(cache, machine).GetAsync();

        Assert.Equal("ready", after.Tools.Single(tool => tool.Id == "pwsh").State);
        Assert.Equal(2, machine.Probes);
    }

    [Fact]
    public void RequiredToolsAndOneAgentMakeSetupComplete()
    {
        var service = Service(new FakeGlobalCache(), new Machine());
        SetupStatusDocument Document(params (string Id, string State)[] tools) =>
            new(SetupStatusDocument.CurrentVersion, "Windows", Now,
                tools.Select(tool => new SetupToolStatusEntry(tool.Id, tool.State, null, null)).ToList());

        Assert.True(service.IsComplete(Document(("git", "ready"), ("pwsh", "restart"), ("grok", "restart"))));
        Assert.False(service.IsComplete(Document(("git", "ready"), ("pwsh", "ready"))));
        Assert.False(service.IsComplete(Document(("git", "ready"), ("pwsh", "missing"), ("claude", "ready"))));
        // Optional tools never count toward the agent requirement.
        Assert.False(service.IsComplete(Document(("git", "ready"), ("pwsh", "ready"), ("node", "ready"))));
    }

    [Fact]
    public async Task RefreshProbesAgainEvenWhenCompleteAndSavesTheNewResult()
    {
        // The install window refreshes after every install, optional tools included.
        var cache = new FakeGlobalCache();
        var machine = new Machine().SetUp();
        await Service(cache, machine).GetAsync();
        machine.Files.Add(Path.Combine(Launch, "node.exe"));

        var refreshed = await Service(cache, machine).RefreshAsync();
        var reread = await Service(cache, machine).GetAsync();

        Assert.Equal(2, machine.Probes);
        Assert.Equal("ready", refreshed.Tools.Single(tool => tool.Id == "node").State);
        Assert.Equal("ready", reread.Tools.Single(tool => tool.Id == "node").State);
    }

    [Fact]
    public async Task ASavedCheckFromAnotherCatalogVersionOrPlatformIsProbedAgain()
    {
        var cache = new FakeGlobalCache();
        var machine = new Machine().SetUp();
        foreach (var stale in new[]
                 {
                     new SetupStatusDocument(SetupStatusDocument.CurrentVersion + 1, "Windows", Now, []),
                     new SetupStatusDocument(SetupStatusDocument.CurrentVersion, "Linux", Now, [])
                 })
        {
            await cache.SetAsync(SetupStatusService.StatusCacheKey,
                JsonSerializer.Serialize(stale, AppJsonSerializerContext.Default.SetupStatusDocument));
            await Service(cache, machine).GetAsync();
        }
        await cache.SetAsync(SetupStatusService.StatusCacheKey, "{ not json");
        await Service(cache, machine).GetAsync();

        Assert.Equal(3, machine.Probes);
    }

    [Fact]
    public async Task ResponseCarriesCatalogMethodsWithTheFirstOneRecommended()
    {
        var status = await Service(new FakeGlobalCache(), new Machine()).GetAsync();

        Assert.Equal("windows", status.Platform);
        var claude = status.Tools.Single(tool => tool.Id == "claude");
        Assert.Equal("agent", claude.Group);
        Assert.True(claude.Methods[0].Recommended);
        Assert.All(claude.Methods.Skip(1), method => Assert.False(method.Recommended));
        Assert.Equal("required", status.Tools.Single(tool => tool.Id == "pwsh").Group);
    }

    private sealed class FakeGlobalCache : IGlobalCache
    {
        public readonly Dictionary<string, string> Values = new();

        public Task<string?> GetAsync(string key) => Task.FromResult(Values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> GetAsBoolAsync(string key, bool defaultValue = false) =>
            Task.FromResult(Values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : defaultValue);

        public Task<int?> GetAsIntAsync(string key) => throw new NotSupportedException();
        public Task<Dictionary<string, string>> GetAllAsync() => Task.FromResult(new Dictionary<string, string>(Values));

        public Task RemoveAsync(string key)
        {
            Values.Remove(key);
            return Task.CompletedTask;
        }
    }
}
