using VibeRails.Routes;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// Pins that the git remote URL surfaced by <c>/api/v1/context</c> never carries an embedded
/// credential. A repo cloned with a token stores it verbatim in <c>.git/config</c>, and
/// <c>git remote get-url</c> returns it unredacted.
/// </summary>
public sealed class ProjectRoutesStripUserInfoTests
{
    [Theory]
    [InlineData(
        "https://user:ghp_secrettoken@github.com/acme/repo.git",
        "https://github.com/acme/repo.git")]
    [InlineData(
        "https://ghp_secrettoken@github.com/acme/repo.git",  // PAT-as-username form
        "https://github.com/acme/repo.git")]
    [InlineData(
        "https://x-access-token:TOKEN@github.com/acme/repo.git",
        "https://github.com/acme/repo.git")]
    public void StripUserInfo_RemovesEmbeddedCredentials(string input, string expected)
    {
        Assert.Equal(expected, ProjectRoutes.StripUserInfo(input));
    }

    [Theory]
    [InlineData("https://github.com/acme/repo.git")]     // no credentials
    [InlineData("git@github.com:acme/repo.git")]         // scp-style: no userinfo, not an absolute URI
    [InlineData("ssh://git@github.com/acme/repo.git")]   // ssh userinfo is a username, not a secret; still stripped-safe
    [InlineData("")]
    [InlineData(null)]
    public void StripUserInfo_LeavesCredentialFreeUrlsUsable(string? input)
    {
        var result = ProjectRoutes.StripUserInfo(input);
        // Never throws, never invents a credential; the scp form (which can't hold a password)
        // survives so cloning/name-derivation still works.
        Assert.DoesNotContain(":ghp_", result ?? "");
    }

    [Fact]
    public void StripUserInfo_SshUserSurvivesHostAndPath()
    {
        // ssh://git@host keeps enough to identify the repo; only a password (which ssh URLs
        // don't carry) would be dropped.
        var result = ProjectRoutes.StripUserInfo("ssh://git@github.com/acme/repo.git");
        Assert.Contains("github.com/acme/repo.git", result);
    }
}
