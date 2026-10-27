using Moq;
using VibeRails.Services.Board;
using VibeRails.Services.Board.PostProcessing;
using VibeRails.Services.Cli;
using VibeRails.Services.Git;
using Xunit;

namespace Tests.Services.Board;

public sealed class GitHubCodeLinkLandingCheckTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "board-link-landing-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ICliWrapper> cli = new(MockBehavior.Strict);
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(true, "main", BoardCommitLanding.Landed)]
    [InlineData(false, "main", BoardCommitLanding.NotLanded)]
    [InlineData(true, "release", BoardCommitLanding.NotLanded)]
    public async Task OnlyMergedPrIntoTheRepositoryDefaultBranchCounts(bool merged, string branch, BoardCommitLanding expected)
    {
        await Init();
        CliRequest? request = null;
        var json = System.Text.Json.JsonSerializer.Serialize(new { data = new { repository = new {
            defaultBranchRef = new { name = "main" }, pr0 = new { merged, baseRefName = branch } } } });
        cli.Setup(c => c.RunAsync(It.IsAny<CliRequest>(), null, It.IsAny<CancellationToken>()))
            .Callback<CliRequest, Func<CliOutputLine, ValueTask>?, CancellationToken>((r, _, _) => request = r)
            .ReturnsAsync(new CliResult(0, false, false, json, "", TimeSpan.Zero, "gh"));
        var result = await new GitHubCodeLinkLandingCheck(new GitDefaultBranchCheck(), cli.Object)
            .CheckAsync(root, [BoardCodeLinks.Parse("https://github.com/org/repo/pull/42")], Ct);
        Assert.Equal(expected, result);
        Assert.Equal("gh", request!.Executable);
        Assert.Contains("owner=org", request.Arguments);
        Assert.Contains("name=repo", request.Arguments);
        Assert.Equal("1", request.EnvironmentVariables!["GH_PROMPT_DISABLED"]);
    }

    private const string NotFound = "{\"type\":\"NOT_FOUND\",\"path\":[\"repository\",\"pr1\"]}";

    [Theory]
    // A deleted PR beside a merged one: the merge still counts even though gh exits 1.
    [InlineData("{\"merged\":true,\"baseRefName\":\"main\"}", "null", "[" + NotFound + "]", false, BoardCommitLanding.Landed)]
    // A bogus PR number is not evidence and must not suppress the ghost.
    [InlineData("{\"merged\":false,\"baseRefName\":\"main\"}", "null", "[" + NotFound + "]", false, BoardCommitLanding.NotLanded)]
    // Any other GraphQL error leaves an unmerged answer unknown.
    [InlineData("{\"merged\":false,\"baseRefName\":\"main\"}", "null", "[{\"type\":\"RATE_LIMITED\"}]", false, BoardCommitLanding.Unverifiable)]
    // A failed exit with no GraphQL errors is not a trustworthy answer either.
    [InlineData("{\"merged\":false,\"baseRefName\":\"main\"}", "{\"merged\":false,\"baseRefName\":\"main\"}", "[]", false, BoardCommitLanding.Unverifiable)]
    [InlineData("{\"merged\":false,\"baseRefName\":\"main\"}", "{\"merged\":false,\"baseRefName\":\"main\"}", "[]", true, BoardCommitLanding.NotLanded)]
    public void EachLinkedPrIsJudgedOnItsOwn(string pr0, string pr1, string errors, bool exitedCleanly, BoardCommitLanding expected)
    {
        var body = "{\"data\":{\"repository\":{\"defaultBranchRef\":{\"name\":\"main\"},\"pr0\":" + pr0
            + ",\"pr1\":" + pr1 + "}},\"errors\":" + errors + "}";
        Assert.Equal(expected, GitHubCodeLinkLandingCheck.ReadPullRequests(body, 2, exitedCleanly));
    }

    [Fact]
    public async Task ForeignRepositoryCannotSuppressGhostOrMakeAGitHubRequest()
    {
        await Init();
        Assert.Equal(BoardCommitLanding.NotLanded, await new GitHubCodeLinkLandingCheck(new GitDefaultBranchCheck(), cli.Object)
            .CheckAsync(root, [BoardCodeLinks.Parse("https://github.com/other/repo/pull/42")], Ct));
        cli.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task LocalUnpushedMainCommitUrlCountsWithoutNetwork()
    {
        await Init();
        await Git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=", "commit", "--allow-empty", "-m", "Local only");
        var sha = (await Git("rev-parse", "HEAD")).Trim();
        Assert.Equal(BoardCommitLanding.Landed, await new GitHubCodeLinkLandingCheck(new GitDefaultBranchCheck(), cli.Object)
            .CheckAsync(root, [BoardCodeLinks.Parse("https://github.com/org/repo/commit/" + sha)], Ct));
        cli.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task MissingGitHubAccessIsUnverifiable()
    {
        await Init();
        cli.Setup(c => c.RunAsync(It.IsAny<CliRequest>(), null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CliResult(4, false, false, "", "auth required", TimeSpan.Zero, "gh"));
        Assert.Equal(BoardCommitLanding.Unverifiable, await new GitHubCodeLinkLandingCheck(new GitDefaultBranchCheck(), cli.Object)
            .CheckAsync(root, [BoardCodeLinks.Parse("https://github.com/org/repo/pull/42")], Ct));
    }

    private async Task Init()
    {
        Directory.CreateDirectory(root);
        await Git("init", "-q", "-b", "main");
        await Git("remote", "add", "origin", "https://github.com/org/repo.git");
    }
    private async Task<string> Git(params string[] args)
    {
        var result = await GitCli.RunAsync(root, args, Ct);
        Assert.True(result.Succeeded, result.StdErr);
        return result.StdOut;
    }
    public void Dispose()
    {
        if (!Directory.Exists(root)) return;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}
