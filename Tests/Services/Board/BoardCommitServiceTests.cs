using System.Text;
using Moq;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Git;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardCommitServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-board-commits-{Guid.NewGuid():N}");
    private readonly BoardCommitService _service = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(0)]
    [InlineData(37)]
    [InlineData(BoardCommitService.MaxFileChars + 1)]
    public async Task GitOutputLimit_RetainsOnlyPrefix_AndDrainsToSuccessfulExit(int limit)
    {
        await InitializeAsync();
        await WriteAsync("large.txt", new string('a', BoardCommitService.MaxFileChars * 5));
        var sha = await CommitAsync();

        var result = await GitCli.RunAsync(_root, ["show", $"{sha}:large.txt"], Ct, maxOutputChars: limit);

        Assert.True(result.Succeeded, result.StdErr);
        Assert.Equal(new string('a', limit), result.StdOut);
        Assert.Empty(result.StdErr);
    }

    [Fact]
    public async Task GitOutputLimit_BoundsErrors_WithoutReportingSuccess()
    {
        await InitializeAsync();
        var result = await GitCli.RunAsync(_root, ["show", "missing-revision"], Ct, maxOutputChars: 10);

        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
        Assert.Empty(result.StdOut);
        Assert.Equal(10, result.StdErr.Length);
    }

    [Fact]
    public async Task GetDiff_LargeModifiedFile_TruncatesBothSides_AndKeepsOtherFiles()
    {
        await InitializeAsync();
        await WriteAsync("large.txt", new string('a', BoardCommitService.MaxFileChars * 5));
        await CommitAsync();
        await WriteAsync("large.txt", new string('b', BoardCommitService.MaxFileChars * 5));
        await WriteAsync("small.cs", "class Example {}\n");
        var sha = await CommitAsync();

        var diff = await _service.GetDiffAsync(_root, sha, Ct);

        Assert.Equal(2, diff.TotalChanges);
        var large = Assert.Single(diff.Files, file => file.FileName == "large.txt");
        Assert.Equal(new string('a', BoardCommitService.MaxFileChars) + "\n… (truncated)", large.OriginalContent);
        Assert.Equal(new string('b', BoardCommitService.MaxFileChars) + "\n… (truncated)", large.ModifiedContent);
        var small = Assert.Single(diff.Files, file => file.FileName == "small.cs");
        Assert.Equal("", small.OriginalContent);
        Assert.Equal("class Example {}\n", small.ModifiedContent);
        Assert.Equal("csharp", small.Language);
    }

    [Fact]
    public async Task GetDiff_RootCommit_PreservesEmptyBinaryAndExactLimitUnicodeFiles()
    {
        await InitializeAsync();
        var exact = new string('é', BoardCommitService.MaxFileChars);
        await WriteAsync("exact.txt", exact);
        await WriteAsync("empty.txt", "");
        await WriteAsync("binary.dat", "binary\0data");
        var sha = await CommitAsync();

        var diff = await _service.GetDiffAsync(_root, sha, Ct);

        Assert.Equal(3, diff.TotalChanges);
        Assert.All(diff.Files, file => Assert.Empty(file.OriginalContent));
        Assert.Equal(exact, Assert.Single(diff.Files, file => file.FileName == "exact.txt").ModifiedContent);
        Assert.Empty(Assert.Single(diff.Files, file => file.FileName == "empty.txt").ModifiedContent);
        Assert.Equal("(binary file)", Assert.Single(diff.Files, file => file.FileName == "binary.dat").ModifiedContent);
    }

    [Fact]
    public async Task GetDiff_DeletedLargeFile_TruncatesOriginal_AndLeavesModifiedEmpty()
    {
        await InitializeAsync();
        await WriteAsync("deleted.txt", new string('d', BoardCommitService.MaxFileChars + 1));
        await CommitAsync();
        await GitAsync("rm", "--", "deleted.txt");
        var sha = await CommitAsync();

        var diff = await _service.GetDiffAsync(_root, sha, Ct);

        var file = Assert.Single(diff.Files);
        Assert.Equal(new string('d', BoardCommitService.MaxFileChars) + "\n… (truncated)", file.OriginalContent);
        Assert.Empty(file.ModifiedContent);
    }

    [Fact]
    public async Task McpLink_CapturesCloneCommit_AndSnapshotSurvivesCloneDeletion()
    {
        await InitializeAsync();
        await WriteAsync("code.cs", "old code\n");
        await CommitAsync();
        var clone = Path.Combine(_root, "workspace");
        await GitAsync("clone", "--", _root, clone);
        await File.WriteAllTextAsync(Path.Combine(clone, "code.cs"), "new code\n", Ct);
        var staged = await GitCli.RunAsync(clone, ["add", "--", "code.cs"], Ct);
        Assert.True(staged.Succeeded, staged.StdErr);
        var committed = await GitCli.RunAsync(clone,
            ["-c", "user.name=Board Tests", "-c", "user.email=board-tests@example.invalid",
             "-c", "core.hooksPath=", "-c", "commit.gpgsign=false", "commit", "-m", "Clone-only change"], Ct);
        Assert.True(committed.Succeeded, committed.StdErr);
        var sha = (await GitCli.RunAsync(clone, ["rev-parse", "HEAD"], Ct)).StdOut.Trim();
        await Assert.ThrowsAsync<BoardValidationException>(() => _service.DescribeAsync(_root, sha, Ct));
        await File.WriteAllTextAsync(Path.Combine(clone, "code.cs"), "unsaved working-tree edits\n", Ct);

        var connectionString = $"Data Source={Path.Combine(_root, "board.db")};Pooling=False";
        var store = new BoardStore(connectionString);
        var board = new BoardService(store, _service, new NullBoardLiveSessionProbe());
        await board.GetColumnsAsync(_root, Ct);
        var card = await board.CreateCardAsync(_root, new CreateBoardCardRequest(Title: "Fix code"), Ct);
        await store.LinkSessionAsync(_root, card.Id, "clone-session", null, "base:claude", "claude", "Clone session", BoardSessionRecord.LaunchOrigin, Ct);
        var tool = new BoardTool(board, new CloneResolver(_root, clone), store);

        var result = await tool.LinkBoardCommit(sha[..7], cancellationToken: Ct);
        Assert.StartsWith($"Linked {sha[..7]}", result);
        Assert.Equal(sha, Assert.Single((await board.GetCardAsync(_root, card.Id, Ct))!.Commits).Sha);
        Assert.Empty(await store.GetCardsAsync(clone, Ct));
        DeleteDirectory(clone);

        // A new dashboard/store instance reads saved code; any call to git is a test failure.
        var reopened = new BoardService(new BoardStore(connectionString), new Mock<IBoardCommitService>(MockBehavior.Strict).Object, new NullBoardLiveSessionProbe());
        var snapshot = await reopened.GetCommitDiffAsync(_root, card.Id, sha, Ct);
        var file = Assert.Single(snapshot!.Files);
        Assert.Equal(("code.cs", "old code\n", "new code\n"), (file.FileName, file.OriginalContent, file.ModifiedContent));
    }

    [Fact]
    public async Task GetDiff_Rename_PreservesUnicodeAndSpaces()
    {
        await InitializeAsync();
        await WriteAsync("old name.txt", "unchanged content\n");
        await CommitAsync();
        await GitAsync("mv", "--", "old name.txt", "café moved.txt");
        var sha = await CommitAsync();

        var file = Assert.Single((await _service.GetDiffAsync(_root, sha, Ct)).Files);
        Assert.Equal("café moved.txt", file.FileName);
        Assert.Equal("unchanged content\n", file.OriginalContent);
        Assert.Equal(file.OriginalContent, file.ModifiedContent);
    }

    [Fact]
    public async Task GetDiff_Merge_CapturesChangesAgainstFirstParent()
    {
        await InitializeAsync();
        await WriteAsync("base.txt", "base");
        await CommitAsync();
        var main = (await GitAsync("branch", "--show-current")).Trim();
        await GitAsync("checkout", "-b", "feature");
        await WriteAsync("feature.txt", "feature code");
        await CommitAsync();
        await GitAsync("checkout", main);
        await WriteAsync("main.txt", "main code");
        await CommitAsync();
        await GitAsync("-c", "core.hooksPath=", "-c", "commit.gpgsign=false", "merge", "--no-ff", "feature", "-m", "Merge feature");
        var sha = (await GitAsync("rev-parse", "HEAD")).Trim();

        var file = Assert.Single((await _service.GetDiffAsync(_root, sha, Ct)).Files);
        Assert.Equal("feature.txt", file.FileName);
        Assert.Equal("feature code", file.ModifiedContent);
        Assert.Empty(file.OriginalContent);
    }

    [Fact]
    public async Task GetDiff_MissingBlob_FailsInsteadOfSavingEmptyContent()
    {
        await InitializeAsync();
        await WriteAsync("lost.txt", "required code");
        var sha = await CommitAsync();
        var blob = (await GitAsync("rev-parse", $"{sha}:lost.txt")).Trim();
        var objectPath = Path.Combine(_root, ".git", "objects", blob[..2], blob[2..]);
        File.SetAttributes(objectPath, FileAttributes.Normal);
        File.Delete(objectPath);

        await Assert.ThrowsAsync<BoardValidationException>(() => _service.GetDiffAsync(_root, sha, Ct));
    }

    [Fact]
    public async Task GetDiff_TooManyFiles_RejectsInsteadOfSilentlyOmittingCode()
    {
        await InitializeAsync();
        for (var i = 0; i <= BoardCommitService.MaxFiles; i++)
            await WriteAsync($"file{i}.txt", "code");
        var sha = await CommitAsync();

        var error = await Assert.ThrowsAsync<BoardValidationException>(() => _service.GetDiffAsync(_root, sha, Ct));
        Assert.Contains("at most 60", error.Message);
    }

    private sealed class CloneResolver(string project, string clone) : IBoardProjectResolver
    {
        public string? CurrentSessionId => "clone-session";
        public string GitWorkingDirectory => clone;
        public Task<string> ResolveAsync(CancellationToken cancellationToken = default) => Task.FromResult(project);
    }

    private async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        await GitAsync("init");
        await GitAsync("config", "user.email", "board-tests@example.invalid");
        await GitAsync("config", "user.name", "Board Tests");
        await GitAsync("config", "core.autocrlf", "false");
    }

    private Task WriteAsync(string name, string content) =>
        File.WriteAllTextAsync(Path.Combine(_root, name), content, new UTF8Encoding(false), Ct);

    private async Task<string> CommitAsync()
    {
        await GitAsync("add", "--all");
        await GitAsync("-c", "core.hooksPath=", "-c", "commit.gpgsign=false", "commit", "-m", "Board diff fixture");
        return (await GitAsync("rev-parse", "HEAD")).Trim();
    }

    private async Task<string> GitAsync(params string[] args)
    {
        var result = await GitCli.RunAsync(_root, args, Ct);
        Assert.True(result.Succeeded, result.StdErr);
        return result.StdOut;
    }

    public void Dispose() => DeleteDirectory(_root);

    private static void DeleteDirectory(string directory)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(directory, recursive: true);
    }
}
