using Moq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Board;
using VibeRails.Services.Mcp;
using Xunit;

namespace Tests.Services.Mcp;

public sealed class DesktopMcpActivityTrackerTests
{
    [Fact]
    public async Task StdioEndOfInputStopsTheHostAndEndsOwnedActivity()
    {
        var store = new Mock<IBoardStore>();
        store.Setup(s => s.StartDesktopActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var tracker = new DesktopMcpActivityTracker(store.Object, true, _ => null, TimeProvider.System);
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(tracker);
        builder.Services.AddHostedService(provider => provider.GetRequiredService<DesktopMcpActivityTracker>());
        using var input = new MemoryStream(); // Immediate EOF is a disconnected stdio client.
        using var output = new MemoryStream();
        builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "presence-eof-test", Version = "1" })
            .WithStreamServerTransport(input, output);
        using var host = builder.Build();
        Assert.True(await tracker.TrackAsync(null, "project", "card", "Codex", TestContext.Current.CancellationToken));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        await host.RunAsync(timeout.Token);

        Assert.False(timeout.IsCancellationRequested); // SDK stopped on EOF, not the test's timeout.
        store.Verify(s => s.EndDesktopActivityAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>()), Times.Once);
        store.Verify(s => s.RenewDesktopActivityAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task StdioKeepsItsOwnedLeaseAliveAndEndsItOnCleanShutdown()
    {
        var store = new Mock<IBoardStore>(MockBehavior.Strict);
        var clock = new Clock();
        string? clientId = null;
        var expiry = default(DateTime);
        store.Setup(s => s.StartDesktopActivityAsync("project", "card", It.IsAny<string>(), "Codex", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, DateTime, CancellationToken>((_, _, id, _, expires, _) => { clientId = id; expiry = expires; })
            .ReturnsAsync(true);
        store.Setup(s => s.RenewDesktopActivityAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        store.Setup(s => s.EndDesktopActivityAsync(It.IsAny<string>(), null, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        using var tracker = new DesktopMcpActivityTracker(store.Object, true, _ => null, clock);
        Assert.True(await tracker.TrackAsync(null, "project", "card", "Codex", TestContext.Current.CancellationToken));
        Assert.NotNull(clientId);
        Assert.Equal(clock.Now.UtcDateTime.AddSeconds(90), expiry);
        clock.Now = clock.Now.AddSeconds(20);
        await tracker.RenewAsync(TestContext.Current.CancellationToken);
        store.Verify(s => s.RenewDesktopActivityAsync(clientId, clock.Now.UtcDateTime.AddSeconds(90), It.IsAny<CancellationToken>()), Times.Once);
        await tracker.StopAsync(TestContext.Current.CancellationToken);
        store.Verify(s => s.EndDesktopActivityAsync(clientId, null, It.IsAny<CancellationToken>()), Times.Once);
        Assert.False(await tracker.TrackAsync(null, "project", "card", "Codex", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HttpAggregateUsesOneOpaqueAppIdentityAndDoesNotHeartbeatOrClearAnotherInstance()
    {
        var store = new Mock<IBoardStore>(MockBehavior.Strict);
        var ids = new List<string>();
        store.Setup(s => s.StartDesktopActivityAsync("project", "card", It.IsAny<string>(), "Codex", It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string, DateTime, CancellationToken>((_, _, id, _, _, _) => ids.Add(id)).ReturnsAsync(true);
        using var tracker = new DesktopMcpActivityTracker(store.Object, false, _ => null, new Clock());
        Assert.True(await tracker.TrackAsync(null, "project", "card", "Codex", TestContext.Current.CancellationToken));
        Assert.True(await tracker.TrackAsync(null, "project", "card", "Codex", TestContext.Current.CancellationToken));
        Assert.Equal(ids[0], ids[1]);
        Assert.DoesNotContain("Codex", ids[0]);
        Assert.NotEqual(ids[0], tracker.ClientId(null, "Claude"));
        Assert.True(tracker.IsAggregate(null));
        await tracker.RenewAsync(TestContext.Current.CancellationToken);
        Assert.False(await tracker.EndAsync(null, "card", "Codex", TestContext.Current.CancellationToken));
        store.Verify(s => s.RenewDesktopActivityAsync(It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
        store.Verify(s => s.EndDesktopActivityAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("real-terminal-tab", false)]
    public async Task OnlyARealTabSuppressesDesktopActivity(string? tabId, bool desktop)
    {
        var store = new Mock<IBoardStore>();
        store.Setup(s => s.StartDesktopActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        using var tracker = new DesktopMcpActivityTracker(store.Object, true,
            name => name == LocalToolApiContext.CurrentTabIdVariable ? tabId : "inherited-session-id", new Clock());
        Assert.Equal(desktop, tracker.IsDesktop);
        Assert.Equal(desktop, await tracker.TrackAsync(null, "project", "card", "Codex", TestContext.Current.CancellationToken));
        store.Verify(s => s.StartDesktopActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), desktop ? Times.Once() : Times.Never());
    }

    [Fact]
    public async Task PresenceFailureIsBestEffort()
    {
        var store = new Mock<IBoardStore>();
        store.Setup(s => s.StartDesktopActivityAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>())).ThrowsAsync(new InvalidOperationException("busy"));
        using var tracker = new DesktopMcpActivityTracker(store.Object, true, _ => null, new Clock());
        Assert.False(await tracker.TrackAsync(null, "project", "card", "Codex", TestContext.Current.CancellationToken));
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
