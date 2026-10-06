using VibeRails.Routes;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Tests.Routes;

public sealed class AuthRoutesTests
{
    [Theory]
    [InlineData("http", "localhost", true)]
    [InlineData("http", "127.0.0.1", false)]
    [InlineData("http", "[::1]", false)]
    [InlineData("http", "example.com", true)]
    [InlineData("https", "127.0.0.1", true)]
    [InlineData("https", "[::1]", true)]
    public void SecureCookieIsRequiredExceptOnNumericLoopbackHttp(string scheme, string host, bool expected)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = scheme;
        context.Request.Host = new HostString(host);
        Assert.Equal(expected, AuthRoutes.UseSecureCookie(context.Request));
    }

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
