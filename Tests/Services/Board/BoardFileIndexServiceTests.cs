using System.Text;
using Tests.Services.Terminal;
using VibeRails.Services.Board;
using VibeRails.Services.Git;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// The repo file index behind the composer's <c>@path</c> typeahead: git listing, the non-git
/// walk, index-only deletions, ranking, caps and the ten-second cache.
/// </summary>
public sealed class BoardFileIndexServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-board-files-{Guid.NewGuid():N}");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GitPath_ListsTrackedAndUntracked_SkipsIgnoredAndIndexOnlyDeletions_AndRanksNamesFirst()
    {
        await InitializeRepositoryAsync();
        await WriteAsync("src/Board/BoardService.cs", "class A {}");
        await WriteAsync("src/Board/Helpers.cs", "class B {}");
        await WriteAsync("docs/Board.md", "# gone soon");
        await WriteAsync("README.md", "readme");
        await WriteAsync(".gitignore", "bin/\n");
        await WriteAsync("bin/ignored.dll", "x");
        await GitAsync("add", "--all");
        // Untracked but not ignored: still a file the agent can open.
        await WriteAsync("notes/board-plan.txt", "plan");
        // In the index, gone from disk: --cached still lists it, the index must not.
        File.Delete(Path.Combine(_root, "docs", "Board.md"));

        var service = new BoardFileIndexService();
        var result = await service.SearchAsync(_root, "board", Ct);

        Assert.Equal(["notes/board-plan.txt", "src/Board/BoardService.cs", "src/Board/Helpers.cs"], result.Files);
        Assert.False(result.Truncated);

        var everything = await service.SearchAsync(_root, "", Ct);
        Assert.Equal([".gitignore", "notes/board-plan.txt", "README.md", "src/Board/BoardService.cs", "src/Board/Helpers.cs"], everything.Files);
        Assert.DoesNotContain(everything.Files, path => path.Contains("bin/", StringComparison.Ordinal));
        Assert.All(everything.Files, path => Assert.DoesNotContain('\\', path));
    }

    [Fact]
    public async Task FallbackPath_WalksDirectories_SkippingVcsAndBuildFolders()
    {
        Directory.CreateDirectory(_root);
        await WriteAsync("src/a.cs", "a");
        await WriteAsync("LICENSE", "mit");
        await WriteAsync(".git/HEAD", "ref: refs/heads/main");
        await WriteAsync("bin/a.dll", "x");
        await WriteAsync("obj/a.o", "x");
        await WriteAsync("node_modules/pkg/index.js", "x");
        await WriteAsync("src/bin/keep.txt", "nested bin is still skipped");

        // The lister answering null is what a missing git, a non-repository or a timeout look like.
        var service = new BoardFileIndexService(TimeProvider.System, (_, _) => Task.FromResult<IReadOnlyList<string>?>(null));
        var result = await service.SearchAsync(_root, null, Ct);

        Assert.Equal(["LICENSE", "src/a.cs"], result.Files);
    }

    [Fact]
    public async Task Search_RanksFileNameHitsBeforeDirectoryHits_CapsAtFifty_AndAcceptsBackslashes()
    {
        Directory.CreateDirectory(_root);
        var listing = Enumerable.Range(1, 70).Select(i => $"deep/board/file{i:00}.cs")
            .Append("top/Board.cs").Append("other/readme.md").ToList();
        var service = new BoardFileIndexService(TimeProvider.System, (_, _) => Task.FromResult<IReadOnlyList<string>?>(listing));

        var result = await service.SearchAsync(_root, "BOARD", Ct);
        Assert.Equal(BoardFileIndexService.MaxResults, result.Files.Count);
        Assert.True(result.Truncated);
        Assert.Equal("top/Board.cs", result.Files[0]);
        Assert.Equal("deep/board/file01.cs", result.Files[1]);

        var windowsStyle = await service.SearchAsync(_root, @"deep\board\file7", Ct);
        Assert.Equal(["deep/board/file70.cs"], windowsStyle.Files);
        Assert.False(windowsStyle.Truncated);

        var nothing = await service.SearchAsync(_root, "zzz", Ct);
        Assert.Empty(nothing.Files);
    }

    [Fact]
    public async Task Search_RejectsAnOversizedQuery_AndAnswersEmptyForAMissingRoot()
    {
        var service = new BoardFileIndexService(TimeProvider.System, (_, _) => Task.FromResult<IReadOnlyList<string>?>(["a.cs"]));
        await Assert.ThrowsAsync<BoardValidationException>(() =>
            service.SearchAsync(_root, new string('q', BoardFileIndexService.MaxQueryLength + 1), Ct));
        var missing = await service.SearchAsync(Path.Combine(_root, "does-not-exist"), "a", Ct);
        Assert.Empty(missing.Files);
    }

    [Fact]
    public async Task Cache_ReusesTheListingForTenSeconds_ThenRefreshes_PerRoot()
    {
        Directory.CreateDirectory(_root);
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero));
        var calls = new List<string>();
        var service = new BoardFileIndexService(clock, (root, _) =>
        {
            calls.Add(root);
            return Task.FromResult<IReadOnlyList<string>?>([$"{calls.Count}.cs"]);
        });

        Assert.Equal(["1.cs"], (await service.SearchAsync(_root, "", Ct)).Files);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(["1.cs"], (await service.SearchAsync(_root, "cs", Ct)).Files);
        Assert.Single(calls);

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(["2.cs"], (await service.SearchAsync(_root, "", Ct)).Files);
        Assert.Equal(2, calls.Count);

        // A different root is never served from another root's list.
        Assert.Equal(["3.cs"], (await service.SearchAsync(other, "", Ct)).Files);
        Assert.Equal([_root, _root, other], calls);
    }

    private async Task InitializeRepositoryAsync()
    {
        Directory.CreateDirectory(_root);
        await GitAsync("init");
        await GitAsync("config", "user.email", "board-tests@example.invalid");
        await GitAsync("config", "user.name", "Board Tests");
        await GitAsync("config", "core.autocrlf", "false");
    }

    private async Task WriteAsync(string relative, string content)
    {
        var path = Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), Ct);
    }

    private async Task<string> GitAsync(params string[] args)
    {
        var result = await GitCli.RunAsync(_root, args, Ct);
        Assert.True(result.Succeeded, result.StdErr);
        return result.StdOut;
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
