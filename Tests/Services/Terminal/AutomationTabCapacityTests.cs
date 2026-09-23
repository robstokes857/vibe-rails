using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.AgentTools;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

public sealed class AutomationTabCapacityTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullHostReclaimsOnlyOldestConfirmedFinishedAutomation(bool unresponsiveOldest)
    {
        var jobs = new Mock<IJobStore>(MockBehavior.Strict);
        foreach (var (id, status) in new[] { ("running", JobRunStatus.Running), ("unavailable", JobRunStatus.Succeeded), ("oldest", JobRunStatus.Succeeded), ("newer", JobRunStatus.Succeeded) })
            jobs.Setup(store => store.GetRunAsync(id, It.IsAny<CancellationToken>())).ReturnsAsync(Run(id, status));
        var sessions = new Mock<ISessionStore>(MockBehavior.Strict);
        sessions.Setup(store => store.GetSessionByIdAsync("outer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionResponse("outer", "shell", null, "/project", DateTime.UtcNow, DateTime.UtcNow, 0));
        using var services = new ServiceCollection().AddSingleton(jobs.Object).AddSingleton(sessions.Object).BuildServiceProvider();
        var requests = new ConcurrentQueue<string>();
        using var http = new StatusHandler(async (request, token) =>
        {
            requests.Enqueue($"{request.Method} {request.RequestUri!.Port}{request.RequestUri.AbsolutePath}");
            if (request.RequestUri.Port == 10003 && unresponsiveOldest)
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(request.RequestUri.Port == 10003 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent("{\"hasActiveSession\":false}")
            };
        });
        var host = Host(services, http);
        var registry = Registry(host);
        using var placeholder = new Process();
        using var childProcess = StartHarmlessProcess();
        try
        {
            for (var i = 0; i < 95; i++)
                AddChild(registry, $"ordinary-{i}", placeholder, i, null);
            AddChild(registry, "starting", childProcess, 1, new AutomationTabState("starting", "Starting"));
            AddChild(registry, "running", childProcess, 2, Started("running"));
            AddChild(registry, "unavailable", childProcess, 3, Started("unavailable"));
            AddChild(registry, "oldest", childProcess, 4, Started("oldest"));
            var newer = Started("newer");
            AddChild(registry, "newer", childProcess, 5, newer);
            Assert.Equal(host.MaxTabs, registry.Count);

            await ReclaimIfFullAsync(host);

            Assert.Equal(99, registry.Count);
            Assert.False(registry.Contains("oldest"));
            Assert.True(registry.Contains("newer"));
            Assert.True(registry.Contains("running"));
            Assert.True(registry.Contains("unavailable"));
            Assert.True(registry.Contains("starting"));
            Assert.All(Enumerable.Range(0, 95), i => Assert.True(registry.Contains($"ordinary-{i}")));
            Assert.True(childProcess.HasExited);
            Assert.Equal(["GET 10003/api/v1/terminal/status", "GET 10004/api/v1/terminal/status", "GET 10005/api/v1/terminal/status", "POST 10004/api/v1/terminal/stop"], requests.Order());
            newer.BeginSessionStart(); // Probing multiple finished hosts reserves only the selected one.
            newer.EndSessionStart(new(true, "new-recording"));
            jobs.VerifyAll();
            sessions.VerifyAll();
        }
        finally
        {
            // Registry placeholders are not real host processes. Never send teardown to them.
            registry.Clear();
            await host.DisposeAsync();
            if (!childProcess.HasExited)
            {
                childProcess.Kill(entireProcessTree: true);
                await childProcess.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
        }
    }

    [Fact]
    public async Task NinetyNineTabsAreRetainedAndFullOrdinaryHostRejectsCreationWithoutEviction()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var http = new StatusHandler(_ => throw new InvalidOperationException("No child calls expected"));
        var host = Host(services, http);
        var registry = Registry(host);
        using var placeholder = new Process();
        try
        {
            for (var i = 0; i < 99; i++)
                AddChild(registry, $"ordinary-{i}", placeholder, i, null);
            await ReclaimIfFullAsync(host);
            Assert.Equal(99, registry.Count);
            AddChild(registry, "ordinary-last", placeholder, 100, null);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.CreateTabAsync(TestContext.Current.CancellationToken));
            Assert.Equal("Maximum of 100 terminal tabs reached.", error.Message);
            Assert.Equal(100, registry.Count);
        }
        finally
        {
            registry.Clear();
            await host.DisposeAsync();
        }
    }

    private static TerminalTabHostService Host(ServiceProvider services, HttpMessageHandler http)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(value => value.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(http, disposeHandler: false));
        return new TerminalTabHostService(factory.Object, Mock.Of<ILocalClientTracker>(), Mock.Of<IAppEventBus>(),
            Mock.Of<ILocalToolApiContext>(), Mock.Of<ITokenSavingsStore>(), services.GetRequiredService<IServiceScopeFactory>());
    }

    private static IDictionary Registry(TerminalTabHostService host) => (IDictionary)typeof(TerminalTabHostService)
        .GetField("_tabs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;

    private static void AddChild(IDictionary registry, string id, Process process, int order, AutomationTabState? automation)
    {
        var childType = typeof(TerminalTabHostService).GetNestedType("TerminalChildProcess", BindingFlags.NonPublic)!;
        registry.Add(id, Activator.CreateInstance(childType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
            [id, process, 10000 + order, "", "session-token", "tab-token", DateTime.UnixEpoch.AddSeconds(order), automation], null)!);
    }

    private static Task ReclaimIfFullAsync(TerminalTabHostService host) => (Task)typeof(TerminalTabHostService)
        .GetMethod("ReclaimCompletedAutomationTabIfFullAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(host, [TestContext.Current.CancellationToken])!;

    private static AutomationTabState Started(string id)
    {
        var state = new AutomationTabState(id, id);
        state.BeginSessionStart();
        state.EndSessionStart(new(true, "outer", "shell", "/project"));
        return state;
    }

    private static JobRunRecord Run(string id, JobRunStatus status) => new(id, 1, JobTriggerKind.Manual, $"manual:{id}",
        status, id, "/project", LLM.NotSet, null, null, null, null, DateTime.UtcNow, DateTime.UtcNow,
        DateTime.UtcNow, 0, null, false, null, TerminalSessionId: "outer");

    private static Process StartHarmlessProcess()
    {
        var start = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        foreach (var arg in OperatingSystem.IsWindows()
            ? new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 120" }
            : new[] { "-c", "sleep 120" })
            start.ArgumentList.Add(arg);
        return Process.Start(start)!;
    }

    private sealed class StatusHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public StatusHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
            : this((request, _) => Task.FromResult(respond(request))) { }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => respond(request, cancellationToken);
    }
}
