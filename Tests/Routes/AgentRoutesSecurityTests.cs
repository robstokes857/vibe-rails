using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.Auth;
using VibeRails.DB;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services;
using Xunit;

namespace Tests.Routes;

public sealed class AgentRoutesSecurityTests : IAsyncLifetime
{
    private readonly string _sandbox = Path.Combine(Path.GetTempPath(), "viberails-agent-routes-" + Guid.NewGuid().ToString("N"));
    private readonly AuthService _auth = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private string _root = null!;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _root = Path.Combine(_sandbox, "repo");
        Directory.CreateDirectory(_root);
        var git = new Mock<IGitService>();
        git.Setup(x => x.GetRootPathAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_root);
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IAuthService>(_auth);
        builder.Services.AddSingleton(git.Object);
        builder.Services.AddSingleton<IAgentFileService>(new AgentFileService(git.Object, new RulesService()));
        builder.Services.AddSingleton(Mock.Of<IRepository>());
        builder.Services.AddSingleton(Mock.Of<IRuleValidationService>());
        _app = builder.Build();
        _app.UseMiddleware<CookieAuthMiddleware>();
        AgentRoutes.Map(_app);
        AuthRoutes.Map(_app);
        await _app.StartAsync(Ct);
        var address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        _client = new HttpClient { BaseAddress = new Uri(address) };
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RuleReadsRequireBothCredentialsAndPermitAnOrdinaryRepositoryFile(bool session, bool tab)
    {
        var path = Path.Combine(_root, "vc.rules.md");
        await File.WriteAllTextAsync(path, "## Vibe Rails Rules\n- [WARN] Log all file changes\n", Ct);
        SetCredentials(session, tab);
        using var response = await _client.GetAsync("/api/v1/agents/rules?path=" + Uri.EscapeDataString(path), Ct);
        Assert.Equal(session && tab ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/rules")]
    [InlineData("GET", "/content")]
    [InlineData("GET", "/files")]
    [InlineData("POST", "/validate")]
    [InlineData("PUT", "/name")]
    [InlineData("POST", "/rules")]
    [InlineData("DELETE", "/rules")]
    [InlineData("PUT", "/rules/enforcement")]
    public async Task AllPathEndpointsRejectForeignFilesWithoutReadingOrChangingThem(string method, string suffix)
    {
        var path = Path.Combine(_sandbox, "vc.rules.md");
        const string original = "## Vibe Rails Rules\n- [WARN] Log all file changes\n";
        await File.WriteAllTextAsync(path, original, Ct);
        SetCredentials(true, true);
        using var request = new HttpRequestMessage(new HttpMethod(method), "/api/v1/agents" + suffix + "?path=" + Uri.EscapeDataString(path));
        if (method != "GET" && suffix != "/validate")
            request.Content = JsonContent.Create(new { path, customName = "renamed", ruleText = "Log all file changes", rules = new[] { "Log all file changes" }, enforcement = "STOP" });
        using var response = await _client.SendAsync(request, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(original, await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(original, await File.ReadAllTextAsync(path, Ct));
    }

    [Fact]
    public async Task BootstrapSetsSecureHttpOnlyCookieAndConsumesTheCode()
    {
        var code = _auth.GenerateBootstrapCode();
        _client.DefaultRequestHeaders.Host = "localhost";
        using var response = await _client.GetAsync("/auth/bootstrap?code=" + code, Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains("; secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly", cookie, StringComparison.OrdinalIgnoreCase);
        using var replay = await _client.GetAsync("/auth/bootstrap?code=" + code, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        Assert.False(replay.Headers.Contains("Set-Cookie"));
    }

    private void SetCredentials(bool session, bool tab)
    {
        if (session) _client.DefaultRequestHeaders.Add("viberails_session", _auth.GetInstanceToken());
        if (tab) _client.DefaultRequestHeaders.Add("viberails_tab", _auth.GetTabToken());
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        Directory.Delete(_sandbox, recursive: true);
    }
}
