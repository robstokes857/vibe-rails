using VibeRails.Services.PrReviews;
using Xunit;

namespace Tests.Services.PrReviews;

public sealed class GitHubRepositoryTests
{
    [Theory]
    [InlineData("https://github.com/PeakActivity/NEE-Treasury-C3.git", "PeakActivity", "NEE-Treasury-C3")]
    [InlineData("https://github.com/octo/widgets", "octo", "widgets")]
    [InlineData("https://github.com/octo/widgets/", "octo", "widgets")]
    [InlineData("https://user:ghp_secret@github.com/octo/widgets.git", "octo", "widgets")]
    [InlineData("http://www.github.com/octo/widgets.git", "octo", "widgets")]
    [InlineData("git@github.com:octo/widgets.git", "octo", "widgets")]
    [InlineData("git@github.com:octo/widgets", "octo", "widgets")]
    [InlineData("ssh://git@github.com/octo/widgets.git", "octo", "widgets")]
    [InlineData("ssh://git@ssh.github.com:443/octo/my.repo_name-2.git", "octo", "my.repo_name-2")]
    [InlineData("git://github.com/octo/widgets.git", "octo", "widgets")]
    [InlineData("  https://GitHub.com/octo/widgets.git  ", "octo", "widgets")]
    public void Parses_github_remotes(string remote, string owner, string name)
    {
        Assert.True(GitHubRepository.TryParseRemote(remote, out var repository));
        Assert.Equal(new GitHubRepository(owner, name), repository);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://gitlab.com/octo/widgets.git")]
    [InlineData("git@gitlab.com:octo/widgets.git")]
    [InlineData("https://github.com.evil.example/octo/widgets.git")]
    [InlineData("https://github.com/octo")]
    [InlineData("https://github.com/octo/widgets/pull/1")]
    [InlineData("https://github.com/-octo/widgets")]
    [InlineData("https://github.com/octo--x/widgets")]
    [InlineData("https://github.com/octo/..")]
    [InlineData("https://github.com/octo/wid%20gets")]
    [InlineData("C:\\source\\widgets")]
    [InlineData("/srv/git/widgets.git")]
    [InlineData("file:///srv/git/widgets.git")]
    public void Rejects_anything_that_is_not_a_github_repository(string? remote)
    {
        Assert.False(GitHubRepository.TryParseRemote(remote, out _));
    }

    [Fact]
    public void Builds_links_without_the_remote_credentials()
    {
        GitHubRepository.TryParseRemote("https://user:ghp_secret@github.com/octo/widgets.git", out var repository);

        Assert.Equal("octo/widgets", repository.FullName);
        Assert.Equal("https://github.com/octo/widgets", repository.Url);
        Assert.Equal("https://github.com/octo/widgets/pull/7", repository.PullRequestUrl(7));
    }
}
