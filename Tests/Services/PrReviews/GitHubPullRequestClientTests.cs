using Moq;
using VibeRails.DB;
using VibeRails.Services.Cli;
using VibeRails.Services.PrReviews;
using Xunit;

namespace Tests.Services.PrReviews;

public sealed class GitHubPullRequestClientTests
{
    private static readonly GitHubRepository Repo = new("octo", "widgets");
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    [Fact]
    public async Task Lists_open_prs_with_an_explicit_repository_and_argv_only()
    {
        CliRequest? sent = null;
        var cli = Cli(request => sent = request, Success("""
            [{"number":7,"title":"feat: x","author":{"login":"octocat"},"createdAt":"2026-08-01T14:00:00Z",
              "updatedAt":"2026-08-02T10:00:00Z","isDraft":true,"headRefOid":"abc123","url":"https://example.invalid/7"}]
            """));

        var prs = await new GitHubPullRequestClient(cli).ListOpenAsync(Repo, "C:/work", _ct);

        var pr = Assert.Single(prs);
        Assert.Equal(7, pr.Number);
        Assert.Equal("octocat", pr.Author);
        Assert.True(pr.IsDraft);
        Assert.Equal("abc123", pr.HeadSha);
        Assert.Equal(new DateTime(2026, 8, 1, 14, 0, 0, DateTimeKind.Utc), pr.OpenedUtc);
        // The link is built from the parsed repository, never taken from the CLI output.
        Assert.Equal("https://github.com/octo/widgets/pull/7", pr.Url);
        Assert.Equal("gh", sent!.Executable);
        Assert.Equal("C:/work", sent.WorkingDirectory);
        Assert.Equal(["pr", "list", "-R", "octo/widgets"], sent.Arguments.Take(4));
        Assert.Equal("1", sent.EnvironmentVariables!["GH_PROMPT_DISABLED"]);
    }

    [Fact]
    public async Task A_missing_or_signed_out_cli_reports_what_to_fix()
    {
        var cli = Cli(_ => { }, new CliResult(1, false, false, "", "gh: To get started, run: gh auth login", TimeSpan.Zero, "gh"));

        var error = await Assert.ThrowsAsync<GitHubCliException>(() =>
            new GitHubPullRequestClient(cli).ListOpenAsync(Repo, "C:/work", _ct));

        Assert.Contains("octo/widgets", error.Message);
        Assert.Contains("gh auth login", error.Message);
        Assert.Contains("gh auth status", error.Message);
    }

    [Fact]
    public void Ignores_malformed_rows_and_defaults_an_unknown_author()
    {
        var prs = GitHubPullRequestClient.ParseOpenPullRequests("""
            [{"number":0,"createdAt":"2026-08-01T14:00:00Z"},
             {"number":3},
             {"number":4,"title":"t","author":null,"createdAt":"2026-08-01T14:00:00Z"}]
            """, Repo);

        var pr = Assert.Single(prs);
        Assert.Equal(4, pr.Number);
        Assert.Equal("unknown", pr.Author);
    }

    [Fact]
    public void The_states_query_uses_variables_for_the_repository()
    {
        var query = GitHubPullRequestClient.BuildStatesQuery([12, 34]);

        Assert.Contains("repository(owner: $owner, name: $name)", query);
        Assert.Contains("pr12: pullRequest(number: 12)", query);
        Assert.Contains("pr34: pullRequest(number: 34)", query);
        Assert.DoesNotContain("octo", query);
    }

    [Fact]
    public async Task Reads_states_by_number_and_keeps_a_partial_answer()
    {
        CliRequest? sent = null;
        // GitHub fails the request for a number that is not a PR here but still answers the rest.
        var cli = Cli(request => sent = request, new CliResult(1, false, false, """
            {"data":{"repository":{
              "pr12":{"number":12,"state":"MERGED","mergedAt":"2026-08-25T12:00:00Z","closedAt":"2026-08-25T12:00:00Z"},
              "pr34":{"number":34,"state":"OPEN","mergedAt":null,"closedAt":null},
              "pr56":null}},
             "errors":[{"message":"Could not resolve to a PullRequest with the number of 56."}]}
            """, "gh: Could not resolve", TimeSpan.Zero, "gh"));

        var states = await new GitHubPullRequestClient(cli).GetStatesAsync(Repo, "C:/work", [12, 34, 56, 12], _ct);

        Assert.Equal([
            new PrGitHubStateUpdate(12, PrGitHubState.Merged, new DateTime(2026, 8, 25, 12, 0, 0, DateTimeKind.Utc)),
            new PrGitHubStateUpdate(34, PrGitHubState.Open, null),
        ], states);
        Assert.Equal(["api", "graphql"], sent!.Arguments.Take(2));
        Assert.Contains("owner=octo", sent.Arguments);
        Assert.Contains("name=widgets", sent.Arguments);
    }

    [Fact]
    public async Task A_states_failure_with_no_data_at_all_is_an_error()
    {
        var cli = Cli(_ => { }, new CliResult(1, false, false, "", "HTTP 401: Bad credentials", TimeSpan.Zero, "gh"));

        var error = await Assert.ThrowsAsync<GitHubCliException>(() =>
            new GitHubPullRequestClient(cli).GetStatesAsync(Repo, "C:/work", [12], _ct));

        Assert.Contains("Bad credentials", error.Message);
    }

    [Fact]
    public async Task Batches_large_state_lookups()
    {
        var calls = 0;
        var cli = Cli(_ => calls++, Success("""{"data":{"repository":{}}}"""));

        await new GitHubPullRequestClient(cli).GetStatesAsync(Repo, "C:/work",
            Enumerable.Range(1, GitHubPullRequestClient.StateBatchSize + 1).ToList(), _ct);

        Assert.Equal(2, calls);
    }

    private static CliResult Success(string stdout) => new(0, false, false, stdout, "", TimeSpan.Zero, "gh");

    private static ICliWrapper Cli(Action<CliRequest> capture, CliResult result)
    {
        var cli = new Mock<ICliWrapper>(MockBehavior.Strict);
        cli.Setup(c => c.RunAsync(It.IsAny<CliRequest>(), It.IsAny<Func<CliOutputLine, ValueTask>?>(), It.IsAny<CancellationToken>()))
            .Callback((CliRequest request, Func<CliOutputLine, ValueTask>? _, CancellationToken _) => capture(request))
            .ReturnsAsync(result);
        return cli.Object;
    }
}
