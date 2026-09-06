using VibeRails.Services.Diagnostics;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Diagnostics;

[Collection("ProcessEnvIsolation")]
public sealed class FeatureLogOptionsTests : IDisposable
{
    private readonly string _originalStatePath = ParserConfigs.GetStatePath();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "viberails-feature-log-options-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        ParserConfigs.SetStatePath(_originalStatePath);
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task DefaultDirectoryIsResolvedBesideTheStateDatabaseOnFirstUse_NotAtConstruction()
    {
        // The singleton can be built before GlobalRuntimePaths.Initialize has run. It must pick up
        // the state directory chosen later instead of freezing a default at construction time.
        ParserConfigs.SetStatePath(string.Empty);
        await using var log = new FeatureLogService(new FeatureLogOptions { ReadCacheDuration = TimeSpan.Zero });
        var stateDirectory = Path.Combine(_root, "override");
        ParserConfigs.SetStatePath(Path.Combine(stateDirectory, "state.db"));

        await log.StartAsync(TestContext.Current.CancellationToken);
        log.Write("data-upload", "started", "Resolved lazily", "op-1");
        await log.StopAsync(TestContext.Current.CancellationToken);

        Assert.Single(Directory.GetFiles(Path.Combine(stateDirectory, "logs", "features"), "feature-*.jsonl"));
        Assert.Equal("Resolved lazily",
            Assert.Single((await log.ReadAsync(new FeatureLogQuery(), cancellationToken: TestContext.Current.CancellationToken)).Entries).Message);
    }

    [Fact]
    public void UninitializedStatePathFailsLoudlyInsteadOfFallingBackToADefaultDirectory()
    {
        ParserConfigs.SetStatePath(string.Empty);
        Assert.Throws<InvalidOperationException>(() => new FeatureLogOptions().ResolveDirectoryPath());
        Assert.Equal(Path.Combine(_root, "explicit"),
            new FeatureLogOptions { DirectoryPath = Path.Combine(_root, "explicit") }.ResolveDirectoryPath());
    }
}
