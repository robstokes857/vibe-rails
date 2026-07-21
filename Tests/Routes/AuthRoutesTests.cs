using VibeRails.Routes;
using Xunit;

namespace Tests.Routes;

public sealed class AuthRoutesTests
{
    [Theory]
    [InlineData("/")]
    [InlineData("/sessions?selected=42")]
    [InlineData("/git-guard#result")]
    public void NormalizeRedirect_AllowsLocalAbsolutePaths(string redirect)
    {
        Assert.Equal(redirect, AuthRoutes.NormalizeRedirect(redirect));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("/safe\\..\\evil")]
    [InlineData("/safe\nnext")]
    public void NormalizeRedirect_RejectsExternalOrAmbiguousPaths(string? redirect)
    {
        Assert.Equal("/", AuthRoutes.NormalizeRedirect(redirect));
    }
}
