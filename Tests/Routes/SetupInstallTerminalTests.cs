using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.Setup;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// The install socket only ever runs catalog commands. These tests stay on the rejection path:
/// a passing install test would really install software on the machine running the suite.
/// </summary>
public sealed class SetupInstallTerminalTests : IAsyncLifetime
{
    private readonly CancellationTokenSource _timeout = new(TimeSpan.FromSeconds(30));
    private WebApplication _app = null!;
    private CancellationToken Token => _timeout.Token;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(Mock.Of<ISetupStatusService>());
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
        _app = builder.Build();
        _app.UseWebSockets();
        SetupRoutes.Map(_app);
        await _app.StartAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(CancellationToken.None);
        await _app.DisposeAsync();
        _timeout.Dispose();
    }

    [Theory]
    [InlineData("""{"tool":"calc","method":"native","cols":80,"rows":24}""")]
    [InlineData("""{"tool":"claude","method":"shell","cols":80,"rows":24}""")]
    // Extra fields are not part of the request contract; there is no way to send a command.
    [InlineData("""{"tool":"claude","method":"custom","command":"calc.exe","cols":80,"rows":24}""")]
    public async Task UnknownToolOrMethodIsRejectedBeforeAnythingStarts(string request)
    {
        var text = await SendAsync(request);
        Assert.Equal("error:That install option is not available on this machine.", text);
    }

    [Theory]
    [InlineData("""{"tool":"claude","method":"native","cols":2,"rows":24}""")]
    [InlineData("not json")]
    public async Task MalformedRequestIsRejected(string request)
    {
        Assert.Equal("error:Invalid install request.", await SendAsync(request));
    }

    [Fact]
    public void InstallShellRunsTheCatalogTextAsOneArgument()
    {
        var method = SetupToolCatalog.FindMethod(SetupToolCatalog.Current, "claude", "native")!.Value.Method;
        var start = SetupInstallTerminalSession.CreateStartInfo(method);

        Assert.Equal(method.Command, start.ArgumentList[^1]);
        Assert.True(Path.IsPathFullyQualified(start.FileName) || !OperatingSystem.IsWindows());
        if (OperatingSystem.IsWindows())
        {
            Assert.Contains(Path.GetFileName(start.FileName), new[] { "pwsh.exe", "powershell.exe" }, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(["-NoLogo", "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command"], start.ArgumentList.Take(5));
        }
    }

    private async Task<string> SendAsync(string request)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(_app.Urls.Single().Replace("http:", "ws:") + "/api/v1/setup/install/ws"), Token);
        await socket.SendAsync(Encoding.UTF8.GetBytes(request), WebSocketMessageType.Text, true, Token);
        var buffer = new byte[4096];
        var received = await socket.ReceiveAsync(buffer, Token);
        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        return Encoding.UTF8.GetString(buffer, 0, received.Count);
    }
}
