using System.Net;
using VibeRails.Routes;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// The MCP Explorer forwards the dashboard's own credential pair to exactly one destination: the
/// in-process /mcp on the address and port its request arrived on (VIBE-310).
/// </summary>
public sealed class McpExplorerTargetTests
{
    private const int Port = 5123;
    private static readonly IPAddress V4 = IPAddress.Loopback;
    private static readonly IPAddress V6 = IPAddress.IPv6Loopback;

    [Fact]
    public void OwnEndpointIsTheConnectionAddressAndPort()
    {
        Assert.Equal($"http://127.0.0.1:{Port}/mcp", McpExplorerTarget.Own(V4, Port).ToString());
        Assert.Equal($"http://[::1]:{Port}/mcp", McpExplorerTarget.Own(V6, Port).ToString());
        Assert.Equal($"http://127.0.0.1:{Port}/mcp", McpExplorerTarget.Own(V4.MapToIPv6(), Port).ToString());
        // Unknown or non-loopback local addresses never become a destination.
        Assert.Equal($"http://127.0.0.1:{Port}/mcp", McpExplorerTarget.Own(null, Port).ToString());
        Assert.Equal($"http://127.0.0.1:{Port}/mcp", McpExplorerTarget.Own(IPAddress.Parse("192.168.1.20"), Port).ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/mcp")]
    [InlineData("http://localhost:5123/mcp")]
    [InlineData("http://LOCALHOST:5123/mcp/")]
    [InlineData("http://127.0.0.1:5123/mcp")]
    public void TheseAllMeanThisBackendAndGetCredentials(string? endpoint)
    {
        var resolved = McpExplorerTarget.Resolve(endpoint, V4, Port);

        Assert.True(McpExplorerTarget.IsOwn(resolved, V4, Port), resolved.ToString());
        Assert.Equal("127.0.0.1", resolved.Host);
        Assert.Equal(Port, resolved.Port);
    }

    [Theory]
    // Another address in 127.0.0.0/8 on our port: a different process can own it.
    [InlineData("http://127.0.0.2:5123/mcp")]
    [InlineData("http://127.1.2.3:5123/mcp")]
    // The other loopback family: Kestrel may not hold it.
    [InlineData("http://[::1]:5123/mcp")]
    // Our address, another port or path.
    [InlineData("http://127.0.0.1:5124/mcp")]
    [InlineData("http://127.0.0.1:5123/mcp/extra")]
    [InlineData("http://127.0.0.1:5123/other")]
    [InlineData("http://localhost:5124/mcp")]
    [InlineData("http://localhost:5123/elsewhere")]
    [InlineData("https://127.0.0.1:5123/mcp")]
    [InlineData("https://localhost:5123/mcp")]
    // Names that merely look local.
    [InlineData("http://localhost.evil.example:5123/mcp")]
    [InlineData("http://127.0.0.1.evil.example:5123/mcp")]
    [InlineData("http://evil.example:5123/mcp")]
    [InlineData("https://mcp.example.com/mcp")]
    public void EverythingElseIsUsedAsWrittenWithoutCredentials(string endpoint)
    {
        var resolved = McpExplorerTarget.Resolve(endpoint, V4, Port);

        Assert.False(McpExplorerTarget.IsOwn(resolved, V4, Port), resolved.ToString());
        Assert.Equal(new Uri(endpoint), resolved);
    }

    [Fact]
    public void ALocalPathStaysOnThisListenerAndOnlyMcpGetsCredentials()
    {
        var other = McpExplorerTarget.Resolve("/api/v1/health", V6, Port);

        Assert.Equal($"http://[::1]:{Port}/api/v1/health", other.ToString());
        Assert.False(McpExplorerTarget.IsOwn(other, V6, Port));
        Assert.True(McpExplorerTarget.IsOwn(McpExplorerTarget.Resolve("/mcp", V6, Port), V6, Port));
        // The request arrived on IPv4: the IPv6 literal is not "ours" for this request, and vice versa.
        Assert.False(McpExplorerTarget.IsOwn(new Uri($"http://[::1]:{Port}/mcp"), V4, Port));
        Assert.False(McpExplorerTarget.IsOwn(new Uri($"http://127.0.0.1:{Port}/mcp"), V6, Port));
    }

    [Theory]
    [InlineData("ftp://127.0.0.1:5123/mcp")]
    [InlineData("file:///C:/x")]
    [InlineData("javascript:alert(1)")]
    [InlineData("mcp")]
    public void OnlyHttpAndHttpsEndpointsAreAccepted(string endpoint) =>
        Assert.Throws<ArgumentException>(() => McpExplorerTarget.Resolve(endpoint, V4, Port));

    [Fact]
    public void TheRoutesTakeTheirPortFromTheConnectionNotTheHostHeader()
    {
        var routes = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "VibeRails", "Routes", "McpRoutes.cs"));

        Assert.DoesNotContain("Request.Host", routes, StringComparison.Ordinal);
        Assert.Contains("ctx.Connection.LocalPort", routes, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VibeRails.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
