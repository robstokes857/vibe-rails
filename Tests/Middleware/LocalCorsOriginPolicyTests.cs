using VibeRails.Middleware;
using Xunit;

namespace Tests.Middleware;

public sealed class LocalCorsOriginPolicyTests
{
    private const int Port = 4173;

    [Theory]
    [InlineData("http://localhost:4173")]
    [InlineData("http://127.0.0.1:4173")]
    [InlineData("http://[::1]:4173")]
    [InlineData("vscode-webview://2f2a4ef6-880f-4e50-a285-22a116f64abc")]
    public void IsAllowed_AcceptsBoundLoopbackAndVsCodeOrigins(string origin)
    {
        Assert.True(LocalCorsOriginPolicy.IsAllowed(origin, Port));
    }

    [Theory]
    [InlineData("http://localhost:4174")]
    [InlineData("http://127.0.0.1:80")]
    [InlineData("http://localhost:4173.evil.example")]
    [InlineData("https://localhost:4173")]
    [InlineData("https://evil.example")]
    [InlineData("")]
    [InlineData(null)]
    public void IsAllowed_RejectsOtherBrowserOrigins(string? origin)
    {
        Assert.False(LocalCorsOriginPolicy.IsAllowed(origin, Port));
    }
}
