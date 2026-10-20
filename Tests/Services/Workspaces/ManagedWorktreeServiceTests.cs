using Microsoft.Data.Sqlite;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.Services.Git;
using VibeRails.Services.Cli;
using VibeRails.Services.Environments;
using VibeRails.Services.Workspaces;
using Moq;
using Xunit;

namespace Tests.Services.Workspaces;

public sealed class ManagedWorktreeServiceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vb-worktree-test-" + Guid.NewGuid().ToString("N"));
    private readonly MutableClock _clock = new();
    private string Root => Path.Combine(_directory, "source");
    private string Connection => new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "state.db"), Pooling = false }.ToString();
    private ManagedWorktreeStore Store => new(Connection);
    private ManagedWorktreeService Service => new(Store, _clock, Path.Combine(_directory, "worktrees"));
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Root);
        await Git(Root, "init");
        await Git(Root, "config", "user.email", "worktree-test@example.invalid");
        await Git(Root, "config", "user.name", "Worktree test");
        await File.WriteAllTextAsync(Path.Combine(Root, "tracked.txt"), "original", Ct);
        await Git(Root, "add", "tracked.txt");
        await Git(Root, "-c", "core.hooksPath=", "commit", "-m", "fixture");
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_directory, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("", null, 7)]
    [InlineData(" delete_after_days=14", null, 14)]
    [InlineData(" delete_after_days=never", null, null)]
    [InlineData(" path=\"C:\\some path\\task\" delete_after_days=30", "C:\\some path\\task", 30)]
    [InlineData(" path='../task one' DELETE_AFTER_DAYS=NEVER", "../task one", null)]
    public void OptionsHaveUsefulDefaultsAndPreserveLiteralPaths(string input, string? path, int? days)
    {
        Assert.Equal(new WorktreeOptions(path, days), WorktreeOptions.Parse(input));
    }

    [Theory]
    [InlineData(" delete_after_days=0")]
    [InlineData(" delete_after_days=6")]
    [InlineData(" delete_after_days=-7")]
    [InlineData(" delete_after_days=999999999999999")]
    [InlineData(" delete_after_days=7.5")]
    [InlineData(" path=\"\"")]
    [InlineData(" path=\"unfinished")]
    [InlineData(" other=7")]
    [InlineData(" path=one path=two")]
    [InlineData(" path=\"one\"delete_after_days=never")]
    public void InvalidOptionsFailBeforeProvisioning(string input) => Assert.Throws<ArgumentException>(() => WorktreeOptions.Parse(input));

    [Fact]
    public async Task DefaultsCreateIndependentWorktreesFromCommittedHeadAndPersistExpiry()
    {
        await File.WriteAllTextAsync(Path.Combine(Root, "tracked.txt"), "dirty source", Ct);
        var first = await Service.CreateAsync(Root, new(), Ct);
        var second = await Service.CreateAsync(Root, new(), Ct);

        Assert.NotEqual(first.Path, second.Path);
        Assert.NotEqual(first.Branch, second.Branch);
        Assert.StartsWith(Path.Combine(_directory, "worktrees"), first.Path);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(first.Path, "tracked.txt"), Ct));
        Assert.Equal(first.Branch, (await Git(first.Path, "branch", "--show-current")).StdOut.Trim());
        Assert.Equal(_clock.GetUtcNow().UtcDateTime.AddDays(7), first.DeleteAfterUTC);
        Assert.Equal(2, (await Store.ListAsync(Ct)).Count); // A fresh store survives a process restart.
        Assert.Equal("dirty source", await File.ReadAllTextAsync(Path.Combine(Root, "tracked.txt"), Ct));
    }

    [Fact]
    public async Task CustomPathIsLiteralRelativeToRepositoryRootAndMustBeNew()
    {
        const string name = "task 'quotes' ’ $() & spaces";
        var worktree = await Service.CreateAsync(Root, new("../" + name, null), Ct);
        Assert.Equal(Path.Combine(_directory, name), worktree.Path);
        Assert.Null(worktree.DeleteAfterUTC);
        await Assert.ThrowsAsync<ArgumentException>(() => Service.CreateAsync(Root, new("../" + name), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Service.CreateAsync(Root, new("."), Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => Service.CreateAsync(Root, new("nested"), Ct));
        Assert.True(File.Exists(Path.Combine(worktree.Path, "tracked.txt")));
    }

    [Fact]
    public async Task ExpiryRemovesDirtyFilesAfterSevenDaysAndKeepsBranch()
    {
        var worktree = await Service.CreateAsync(Root, new(), Ct);
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "tracked.txt"), "uncommitted", Ct);
        await File.WriteAllTextAsync(Path.Combine(worktree.Path, "untracked.txt"), "untracked", Ct);
        _clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await Service.CleanupExpiredAsync(Ct));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await Service.CleanupExpiredAsync(Ct));
        Assert.False(Directory.Exists(worktree.Path));
        Assert.Empty(await Store.ListAsync(Ct));
        Assert.Equal(0, (await Git(Root, "show-ref", "--verify", "refs/heads/" + worktree.Branch)).ExitCode);
        Assert.Equal(0, await Service.CleanupExpiredAsync(Ct));
    }

    [Fact]
    public async Task NeverAndLongerRetentionSurviveTheDefaultDeadline()
    {
        var forever = await Service.CreateAsync(Root, new(DeleteAfterDays: null), Ct);
        var longer = await Service.CreateAsync(Root, new(DeleteAfterDays: 14), Ct);
        _clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(0, await Service.CleanupExpiredAsync(Ct));
        _clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(1, await Service.CleanupExpiredAsync(Ct));
        Assert.True(Directory.Exists(forever.Path));
        Assert.False(Directory.Exists(longer.Path));
    }

    [Fact]
    public async Task ActiveUsageInAnyProcessPostponesCleanupAndAllowsMultipleSessions()
    {
        var worktree = await Service.CreateAsync(Root, new(), Ct);
        Directory.CreateDirectory(Path.Combine(worktree.Path, "subdir"));
        var first = await Service.AcquireUsageAsync(worktree.Path, Ct);
        var second = await Service.AcquireUsageAsync(Path.Combine(worktree.Path, "subdir"), Ct);
        Assert.NotNull(first);
        Assert.NotNull(second);
        try
        {
            _clock.Advance(TimeSpan.FromDays(8));
            Assert.Equal(0, await Service.CleanupExpiredAsync(Ct));
            first.Dispose();
            Assert.Equal(0, await Service.CleanupExpiredAsync(Ct));
        }
        finally { first.Dispose(); second.Dispose(); }
        Assert.Equal(1, await Service.CleanupExpiredAsync(Ct));
    }

    [Fact]
    public async Task OpenDatabaseSessionAlsoProtectsWorktreesUsedByOlderVersions()
    {
        var worktree = await Service.CreateAsync(Root, new(), Ct);
        using var db = new SqliteConnection(Connection);
        await db.OpenAsync(Ct);
        using var command = db.CreateCommand();
        command.CommandText = "INSERT INTO Sessions (Id, Cli, WorkingDirectory, StartedUTC) VALUES ('active', 'codex', $path, $now);";
        command.Parameters.AddWithValue("$path", worktree.Path);
        command.Parameters.AddWithValue("$now", _clock.GetUtcNow().UtcDateTime.ToString("O"));
        await command.ExecuteNonQueryAsync(Ct);
        _clock.Advance(TimeSpan.FromDays(8));
        Assert.Equal(0, await Service.CleanupExpiredAsync(Ct));
        command.CommandText = "UPDATE Sessions SET EndedUTC=$now WHERE Id='active';";
        await command.ExecuteNonQueryAsync(Ct);
        Assert.Equal(1, await Service.CleanupExpiredAsync(Ct));
    }

    [Fact]
    public async Task ReplacedOwnershipMarkerPreventsDeletion()
    {
        var worktree = await Service.CreateAsync(Root, new(), Ct);
        await File.WriteAllTextAsync(Path.Combine(worktree.GitDirectory, "viberails-worktree-id"), "someone else", Ct);
        _clock.Advance(TimeSpan.FromDays(8));
        Assert.Equal(0, await Service.CleanupExpiredAsync(Ct));
        Assert.True(Directory.Exists(worktree.Path));
        await Assert.ThrowsAsync<IOException>(() => Service.AcquireUsageAsync(worktree.Path, Ct));
    }

    [Fact]
    public async Task UserGitLockIsRespectedAndCleanupRetriesAfterUnlock()
    {
        var worktree = await Service.CreateAsync(Root, new(), Ct);
        await Git(Root, "worktree", "lock", worktree.Path);
        _clock.Advance(TimeSpan.FromDays(8));
        Assert.Equal(0, await Service.CleanupExpiredAsync(Ct));
        await Git(Root, "worktree", "unlock", worktree.Path);
        Assert.Equal(1, await Service.CleanupExpiredAsync(Ct));
    }

    [Fact]
    public async Task TwoRootsRacingForACustomPathCannotAdoptOrRemoveEachOthersCheckout()
    {
        var path = Path.Combine(_directory, "shared-target");
        var attempts = new[] { Service.CreateAsync(Root, new(path), Ct), Service.CreateAsync(Root, new(path), Ct) };
        try { await Task.WhenAll(attempts); }
        catch (Exception) { /* Exactly one reservation wins; inspect both outcomes below. */ }
        Assert.Single(attempts, task => task.IsCompletedSuccessfully);
        Assert.Single(attempts, task => task.IsFaulted);
        Assert.Single(await Store.ListAsync(Ct));
        Assert.True(File.Exists(Path.Combine(path, "tracked.txt")));
        _clock.Advance(TimeSpan.FromDays(8));
        var results = await Task.WhenAll(Service.CleanupExpiredAsync(Ct), Service.CleanupExpiredAsync(Ct));
        Assert.Equal(1, results.Sum());
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task WorktreesShareTheRepositoryAndOutliveTheirSourceWorktree()
    {
        var first = await Service.CreateAsync(Root, new(), Ct);
        var second = await Service.CreateAsync(first.Path, new(DeleteAfterDays: 14), Ct);
        Assert.Equal(first.GitCommonDirectory, second.GitCommonDirectory);
        _clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(1, await Service.CleanupExpiredAsync(Ct));
        Assert.False(Directory.Exists(first.Path));
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(second.Path, "tracked.txt"), Ct));
        _clock.Advance(TimeSpan.FromDays(7));
        Assert.Equal(1, await Service.CleanupExpiredAsync(Ct));
    }

    [Fact]
    public async Task GitBranchPlaceholderResolvesInsideTheNewWorktree()
    {
        var prompts = new PromptPlaceholderService(Mock.Of<IEnvironmentStore>(), Mock.Of<ICliWrapper>(), worktrees: Service);
        var result = await prompts.ResolveLaunchAsync("{{worktree}} on {{git_branch}}", new PromptPlaceholderContext(Root), Ct);
        var worktree = Assert.Single(await Store.ListAsync(Ct));
        Assert.Equal(worktree.Path, result.WorkingDirectory);
        Assert.Equal(worktree.Path + " on " + worktree.Branch, result.Prompt);
    }

    private async Task<GitCliResult> Git(string directory, params string[] args)
    {
        var result = await GitCli.RunAsync(directory, args, Ct);
        Assert.True(result.Succeeded, result.StdErr);
        return result;
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
