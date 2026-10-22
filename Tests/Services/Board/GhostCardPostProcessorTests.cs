using System.Text;
using VibeRails.Services.Board;
using VibeRails.Services.Board.PostProcessing;
using VibeRails.Services.Git;
using Xunit;

namespace Tests.Services.Board;

/// <summary>The ghost rule (VIBE-185) and the local default-branch check it relies on.</summary>
public sealed class GhostCardPostProcessorTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-ghost-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BoardPostProcessingCandidate Card(string type = BoardCardTypes.Task, string lane = "Done", string board = "VibeRails",
        int sessions = 1, int codeLinks = 0, params string[] shas) =>
        new("card_1", @"C:\repo", "VB-ABCDE-1", type, "brd_1", board, "col_done", lane, DateTime.UtcNow, sessions, codeLinks, shas);

    [Fact]
    public async Task WorkWithNothingLinkedIsAGhost()
    {
        var outcome = await new GhostCardPostProcessor(new FixedCheck(BoardCommitLanding.Landed)).ProcessAsync(Card(), Ct);
        Assert.Equal(BoardGhostReasons.NoCode, outcome.GhostReason);
        Assert.False(outcome.Defer);
    }

    [Theory]
    [InlineData(BoardCommitLanding.NotLanded, BoardGhostReasons.NotLanded, false)]
    [InlineData(BoardCommitLanding.Landed, null, false)]
    [InlineData(BoardCommitLanding.Unverifiable, null, false)]
    [InlineData(BoardCommitLanding.Failed, null, true)]
    public async Task LinkedCommitsCountOnceOneIsOnTheDefaultBranch(BoardCommitLanding landing, string? ghost, bool defer)
    {
        var check = new FixedCheck(landing);
        var outcome = await new GhostCardPostProcessor(check).ProcessAsync(Card(shas: ["abc1234"]), Ct);
        Assert.Equal(ghost, outcome.GhostReason);
        Assert.Equal(defer, outcome.Defer);
        Assert.Equal(@"C:\repo", check.Project);
    }

    [Fact]
    public async Task OwnerExceptionsNeverGetAGhostOrTouchGit()
    {
        var check = new FixedCheck(BoardCommitLanding.NotLanded);
        var processor = new GhostCardPostProcessor(check);
        foreach (var card in new[]
        {
            Card(type: BoardCardTypes.ResearchSpike),
            Card(sessions: 0),
            Card(codeLinks: 1, shas: ["abc1234"]),
            Card(lane: "Canceled"),
            Card(board: "Not Doing")
        })
            Assert.Equal(BoardCardPostProcessorOutcome.Nothing, await processor.ProcessAsync(card, Ct));
        Assert.Null(check.Project);
    }

    [Theory]
    [InlineData("Canceled", true)]
    [InlineData("Cancelled", true)]
    [InlineData("Not Doing", true)]
    [InlineData("Won't Do", true)]
    [InlineData("Won\u2019t do", true)]
    [InlineData("WONT-FIX", true)]
    [InlineData("Abandoned", true)]
    [InlineData("Rejected ideas", true)]
    [InlineData("Duplicates", true)]
    [InlineData("Done", false)]
    [InlineData("Shipped", false)]
    [InlineData("Complete", false)]
    [InlineData("Archive", false)]
    [InlineData("", false)]
    public void RecognisesLanesThatMeanNotDone(string name, bool notDoing) =>
        Assert.Equal(notDoing, GhostCardPostProcessor.IsNotDoing(name));

    [Fact]
    public async Task AncestorsOfTheLocalDefaultBranchHaveLanded()
    {
        var repo = await RepoAsync("work");
        var landed = await CommitAsync(repo, "a.txt", "one");
        await GitAsync(repo, "checkout", "-b", "feature");
        var branchOnly = await CommitAsync(repo, "b.txt", "two");
        var check = new GitDefaultBranchCheck();

        Assert.Equal(BoardCommitLanding.Landed, await check.CheckAsync(repo, [landed], Ct));
        Assert.Equal(BoardCommitLanding.NotLanded, await check.CheckAsync(repo, [branchOnly], Ct));
        Assert.Equal(BoardCommitLanding.Landed, await check.CheckAsync(repo, [branchOnly, landed], Ct));
        // A commit this checkout does not have cannot be on its default branch either.
        Assert.Equal(BoardCommitLanding.NotLanded, await check.CheckAsync(repo, [new string('f', 40)], Ct));
    }

    [Fact]
    public async Task AnEquivalentPatchOnTheDefaultBranchCounts()
    {
        var repo = await RepoAsync("work");
        await CommitAsync(repo, "a.txt", "one");
        await GitAsync(repo, "checkout", "-b", "feature");
        var original = await CommitAsync(repo, "b.txt", "two");
        await GitAsync(repo, "checkout", "main");
        await CommitAsync(repo, "c.txt", "three");
        // The PR bot's amend or a rebase lands the same patch under a new SHA.
        await GitAsync(repo, "-c", "core.hooksPath=", "-c", "commit.gpgsign=false", "cherry-pick", original);

        Assert.Equal(BoardCommitLanding.Landed, await new GitDefaultBranchCheck().CheckAsync(repo, [original], Ct));
    }

    [Fact]
    public async Task TheOriginRemoteTrackingRefCountsWithoutAFetch()
    {
        var upstream = await RepoAsync("upstream");
        await CommitAsync(upstream, "a.txt", "one");
        var clone = Path.Combine(root, "clone");
        await GitAsync(root, "clone", "-q", upstream, clone);
        var merged = await CommitAsync(upstream, "b.txt", "merged upstream");
        await GitAsync(clone, "fetch", "-q", "origin");
        var check = new GitDefaultBranchCheck();

        Assert.Equal(new[] { "refs/heads/main", "refs/remotes/origin/main" }, await GitDefaultBranchCheck.DefaultBranchRefsAsync(clone, Ct));
        Assert.Equal(BoardCommitLanding.Landed, await check.CheckAsync(clone, [merged], Ct));
    }

    [Fact]
    public async Task NothingToCheckAgainstIsUnverifiable()
    {
        var check = new GitDefaultBranchCheck();
        Assert.Equal(BoardCommitLanding.Unverifiable, await check.CheckAsync(Path.Combine(root, "missing"), ["abc1234"], Ct));

        var plain = Path.Combine(root, "plain");
        Directory.CreateDirectory(plain);
        Assert.Equal(BoardCommitLanding.Unverifiable, await check.CheckAsync(plain, ["abc1234"], Ct));

        var trunk = await RepoAsync("trunk", branch: "trunk");
        var sha = await CommitAsync(trunk, "a.txt", "one");
        Assert.Equal(BoardCommitLanding.Unverifiable, await check.CheckAsync(trunk, [sha], Ct));
    }

    private async Task<string> RepoAsync(string name, string branch = "main")
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        await GitAsync(path, "init", "-q", "-b", branch);
        await GitAsync(path, "config", "user.email", "board-tests@example.invalid");
        await GitAsync(path, "config", "user.name", "Board Tests");
        await GitAsync(path, "config", "core.autocrlf", "false");
        return path;
    }

    private async Task<string> CommitAsync(string repo, string file, string content)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, file), content, new UTF8Encoding(false), Ct);
        await GitAsync(repo, "add", "--all");
        await GitAsync(repo, "-c", "core.hooksPath=", "-c", "commit.gpgsign=false", "commit", "-q", "-m", "Fixture " + file);
        return (await GitAsync(repo, "rev-parse", "HEAD")).Trim();
    }

    private async Task<string> GitAsync(string directory, params string[] args)
    {
        var result = await GitCli.RunAsync(directory, args, Ct, TimeSpan.FromSeconds(30));
        Assert.True(result.Succeeded, result.StdErr);
        return result.StdOut;
    }

    private sealed class FixedCheck(BoardCommitLanding answer) : IBoardDefaultBranchCheck
    {
        public string? Project { get; private set; }
        public Task<BoardCommitLanding> CheckAsync(string projectPath, IReadOnlyList<string> shas, CancellationToken cancellationToken)
        {
            Project = projectPath;
            return Task.FromResult(answer);
        }
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
