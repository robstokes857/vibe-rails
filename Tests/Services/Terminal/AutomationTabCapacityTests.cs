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
    [InlineData("503")]
    [InlineData("timeout")]
    [InlineData("transport")]
    [InlineData("json")]
    public async Task AgentCloseRetriesTransientStatusFailureWithoutAnotherEvent(string failure)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var probes = 0;
        using var http = new StatusHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/status") && Interlocked.Increment(ref probes) <= 2)
            {
                if (failure == "timeout") throw new TaskCanceledException("status deadline");
                if (failure == "transport") throw new HttpRequestException("connection reset");
                return new HttpResponseMessage(failure == "503" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                    { Content = new StringContent("invalid json") };
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                request.RequestUri.AbsolutePath.EndsWith("/start") ? "{\"hasActiveSession\":true,\"sessionId\":\"agent\"}" : "{\"hasActiveSession\":false}") };
        });
        var bus = new AppEventBus();
        var closed = new TaskCompletionSource<AppEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        var events = new ConcurrentQueue<AppEvent>();
        using var subscription = bus.Subscribe(e => { events.Enqueue(e); closed.TrySetResult(e); });
        var host = Host(services, http, bus);
        var registry = Registry(host);
        using var child = StartHarmlessProcess();
        try
        {
            AddChild(registry, "caller", child, 1, null);
            await host.StartSessionAsync("caller", new(Cli: "codex"), TestContext.Current.CancellationToken);
            Assert.False(await host.CloseAgentTabAsync("caller", "agent"));
            // A duplicate notification must not lose the pending request or remove twice.
            Assert.False(await host.CloseAgentTabAsync("caller", "agent"));
            var result = await closed.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.Equal("agent_terminal_closed", result.Type);
            Assert.Equal("agent", result.Payload.GetProperty("sessionId").GetString());
            Assert.Single(events);
            Assert.False(registry.Contains("caller"));
            Assert.True(child.HasExited);
        }
        finally
        {
            registry.Clear();
            await host.DisposeAsync();
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(TestContext.Current.CancellationToken); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingAgentCloseDoesNotFollowAReplacementOrUncertainStart(bool startFails)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var starts = 0;
        var probes = 0;
        using var http = new StatusHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/start"))
            {
                if (++starts == 2 && startFails) throw new HttpRequestException("unknown start result");
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                    starts == 1 ? "{\"hasActiveSession\":true,\"sessionId\":\"agent\"}" : "{\"hasActiveSession\":true,\"sessionId\":\"replacement\"}") };
            }
            Assert.EndsWith("/status", request.RequestUri.AbsolutePath);
            Interlocked.Increment(ref probes);
            return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        });
        var host = Host(services, http);
        var registry = Registry(host);
        using var child = StartHarmlessProcess();
        try
        {
            AddChild(registry, "caller", child, 1, null);
            await host.StartSessionAsync("caller", new(Cli: "codex"), TestContext.Current.CancellationToken);
            Assert.False(await host.CloseAgentTabAsync("caller", "agent"));
            var replacement = host.StartSessionAsync("caller", new(Cli: "codex"), TestContext.Current.CancellationToken);
            if (startFails) await Assert.ThrowsAsync<InvalidOperationException>(() => replacement);
            else await replacement;
            var pending = (HashSet<string>)registry["caller"]!.GetType().GetProperty("PendingAgentClosures")!.GetValue(registry["caller"])!;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (pending.Count != 0) await Task.Delay(20, timeout.Token);
            Assert.Equal(1, probes); // A stale retry cannot probe or close the replacement.
            Assert.True(registry.Contains("caller"));
            Assert.False(child.HasExited);
        }
        finally
        {
            registry.Clear();
            await host.DisposeAsync();
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(TestContext.Current.CancellationToken); }
        }
    }

    [Fact]
    public async Task AgentCloseRemovesOnlyItsHostOnceAndAnnouncesRetainedSessionIdentity()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var requests = new List<string>();
        using var http = new StatusHandler(request =>
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                request.RequestUri.AbsolutePath.EndsWith("/start") ? "{\"hasActiveSession\":true,\"sessionId\":\"agent\"}" : "{\"hasActiveSession\":false}") };
        });
        var bus = new AppEventBus();
        var events = new List<AppEvent>();
        using var subscription = bus.Subscribe(events.Add);
        var host = Host(services, http, bus);
        var registry = Registry(host);
        using var child = StartHarmlessProcess();
        using var unrelated = new Process();
        try
        {
            AddChild(registry, "caller", child, 1, null);
            AddChild(registry, "ordinary", unrelated, 2, null);
            await host.StartSessionAsync("caller", new(Cli: "codex"), TestContext.Current.CancellationToken);
            Assert.False(await host.CloseAgentTabAsync("caller", "old-agent"));
            Assert.False(child.HasExited);
            Assert.True(await host.CloseAgentTabAsync("caller", "agent"));
            Assert.False(await host.CloseAgentTabAsync("caller", "agent"));
            Assert.True(child.HasExited);
            Assert.True(registry.Contains("ordinary"));
            Assert.False(registry.Contains("caller"));
            var closed = Assert.Single(events);
            Assert.Equal("agent_terminal_closed", closed.Type);
            Assert.Equal("caller", closed.Payload.GetProperty("tabId").GetString());
            Assert.Equal("agent", closed.Payload.GetProperty("sessionId").GetString());
            Assert.Equal(new[] { "/api/v1/terminal/start", "/api/v1/terminal/status", "/api/v1/terminal/stop" }, requests);
            // No session/Board store is registered: removal never deletes recordings or links.
        }
        finally
        {
            registry.Clear();
            await host.DisposeAsync();
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(TestContext.Current.CancellationToken); }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DelayedAgentCloseCannotKillAReplacementOrAnUncertainStart(bool startFails)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        using var http = new StatusHandler(async (request, _) =>
        {
            Assert.EndsWith("/start", request.RequestUri!.AbsolutePath); // Stale close must not even query/stop it.
            if (++starts == 2)
            {
                started.TrySetResult();
                await release.Task;
                if (startFails) throw new HttpRequestException("start result unknown");
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(
                starts == 1 ? "{\"hasActiveSession\":true,\"sessionId\":\"old\"}" : "{\"hasActiveSession\":true,\"sessionId\":\"replacement\"}") };
        });
        var host = Host(services, http);
        var registry = Registry(host);
        using var child = StartHarmlessProcess();
        try
        {
            AddChild(registry, "caller", child, 1, null);
            await host.StartSessionAsync("caller", new(Cli: "codex"), TestContext.Current.CancellationToken);
            var replacement = host.StartSessionAsync("caller", new(Cli: "codex"), TestContext.Current.CancellationToken);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var close = host.CloseAgentTabAsync("caller", "old");
            Assert.False(close.IsCompleted);
            release.TrySetResult();
            if (startFails) await Assert.ThrowsAsync<InvalidOperationException>(() => replacement);
            else await replacement;
            Assert.False(await close);
            Assert.False(await host.CloseAgentTabAsync("caller", "old"));
            Assert.True(registry.Contains("caller"));
            Assert.False(child.HasExited);
        }
        finally
        {
            release.TrySetResult();
            registry.Clear();
            await host.DisposeAsync();
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(TestContext.Current.CancellationToken); }
        }
    }

    [Fact]
    public async Task SessionStartCannotSlipBetweenAgentCloseValidationAndHostRemoval()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        using var http = new StatusHandler(async (request, _) =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/start"))
            {
                starts++;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"hasActiveSession\":true,\"sessionId\":\"agent\"}") };
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/status"))
            {
                checking.TrySetResult();
                await release.Task;
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"hasActiveSession\":false}") };
        });
        var host = Host(services, http);
        var registry = Registry(host);
        using var child = StartHarmlessProcess();
        try
        {
            AddChild(registry, "caller", child, 1, null);
            await host.StartSessionAsync("caller", new(Cli: "codex"), TestContext.Current.CancellationToken);
            var close = host.CloseAgentTabAsync("caller", "agent");
            await checking.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var start = host.StartSessionAsync("caller", new(Cli: "codex"), TestContext.Current.CancellationToken);
            Assert.False(start.IsCompleted);
            release.TrySetResult();
            Assert.True(await close);
            await Assert.ThrowsAsync<InvalidOperationException>(() => start);
            Assert.Equal(1, starts);
            Assert.True(child.HasExited);
        }
        finally
        {
            release.TrySetResult();
            registry.Clear();
            await host.DisposeAsync();
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(TestContext.Current.CancellationToken); }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AgentCloseKeepsAnActiveOrUnavailableChild(bool active)
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var http = new StatusHandler(request => new HttpResponseMessage(
            request.RequestUri!.AbsolutePath.EndsWith("/start") || active ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable)
        { Content = new StringContent("{\"hasActiveSession\":true,\"sessionId\":\"agent\"}") });
        var host = Host(services, http);
        var registry = Registry(host);
        using var child = StartHarmlessProcess();
        try
        {
            AddChild(registry, "caller", child, 1, null);
            await host.StartSessionAsync("caller", new(Cli: "codex"), TestContext.Current.CancellationToken);
            Assert.False(await host.CloseAgentTabAsync("caller", "agent"));
            Assert.True(registry.Contains("caller"));
            Assert.False(child.HasExited);
        }
        finally
        {
            registry.Clear();
            await host.DisposeAsync();
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(TestContext.Current.CancellationToken); }
        }
    }

    [Fact]
    public async Task TabListReportsFinishedWorkflowAndBoundsAnUnresponsiveChild()
    {
        var jobs = new Mock<IJobStore>(MockBehavior.Strict);
        jobs.Setup(s => s.GetRunAsync("finished", It.IsAny<CancellationToken>())).ReturnsAsync(Run("finished", JobRunStatus.Succeeded));
        using var services = new ServiceCollection().AddSingleton(jobs.Object).BuildServiceProvider();
        using var http = new StatusHandler(async (request, token) =>
        {
            if (request.RequestUri!.Port == 10002) await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"hasActiveSession\":true,\"sessionId\":\"outer\"}") };
        });
        var host = Host(services, http);
        var registry = Registry(host);
        using var child = StartHarmlessProcess();
        try
        {
            AddChild(registry, "finished", child, 1, Started("finished"));
            AddChild(registry, "stalled", child, 2, Started("stalled"));
            var tabs = await host.ListTabsAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
            Assert.True(tabs.Single(t => t.TabId == "finished").AutomationCompleted);
            Assert.True(tabs.Single(t => t.TabId == "finished").HasActiveSession);
            Assert.False(tabs.Single(t => t.TabId == "stalled").AutomationCompleted);
            Assert.False(tabs.Single(t => t.TabId == "stalled").StatusAvailable);
            Assert.False(child.HasExited);
            jobs.VerifyAll();
        }
        finally
        {
            registry.Clear();
            await host.DisposeAsync();
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(TestContext.Current.CancellationToken); }
        }
    }

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
    public async Task SchedulerClosesFinishedAutomationBelowCapacityAndKeepsOtherTabs()
    {
        var jobs = new Mock<IJobStore>(MockBehavior.Strict);
        jobs.Setup(s => s.GetRunAsync("finished", It.IsAny<CancellationToken>())).ReturnsAsync(Run("finished", JobRunStatus.Succeeded));
        jobs.Setup(s => s.GetRunAsync("running", It.IsAny<CancellationToken>())).ReturnsAsync(Run("running", JobRunStatus.Running));
        var sessions = new Mock<ISessionStore>(MockBehavior.Strict);
        sessions.Setup(s => s.GetSessionByIdAsync("outer", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionResponse("outer", "shell", null, "/project", DateTime.UtcNow, DateTime.UtcNow, 0));
        using var services = new ServiceCollection().AddSingleton(jobs.Object).AddSingleton(sessions.Object).BuildServiceProvider();
        using var http = new StatusHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"hasActiveSession\":false}") });
        var host = Host(services, http);
        var registry = Registry(host);
        using var placeholder = new Process();
        using var finished = StartHarmlessProcess();
        try
        {
            AddChild(registry, "ordinary", placeholder, 0, null);
            AddChild(registry, "starting", placeholder, 1, new AutomationTabState("starting", "Starting"));
            AddChild(registry, "running", placeholder, 2, Started("running"));
            AddChild(registry, "finished", finished, 3, Started("finished"));
            await host.CloseCompletedAutomationTabsAsync(TestContext.Current.CancellationToken);
            Assert.False(registry.Contains("finished"));
            Assert.True(finished.HasExited);
            Assert.True(registry.Contains("ordinary"));
            Assert.True(registry.Contains("starting"));
            Assert.True(registry.Contains("running"));
            sessions.VerifyAll();
        }
        finally
        {
            registry.Clear();
            await host.DisposeAsync();
            if (!finished.HasExited) { finished.Kill(entireProcessTree: true); await finished.WaitForExitAsync(TestContext.Current.CancellationToken); }
        }
    }

    [Fact]
    public async Task SchedulerClosesFinishedRunWhoseWrapperSessionLingersAndKeepsFreshAndRunningHosts()
    {
        // VIBE-45: a run reaches a final status while its outer `pwsh … vb --job-run …; exit` wrapper never
        // exits, so the child keeps reporting the run's own session as active and the Automation menu shows
        // the finished agent as Running indefinitely. Once the run row is older than the grace window the
        // pass closes that host exactly like a dismissal: graceful stop first, then the process tree. A run
        // that ended moments ago is still given the chance to exit by itself, and a Running run is untouched.
        var past = DateTime.UtcNow - AutomationTabState.LingeringSessionGrace - TimeSpan.FromMinutes(1);
        var jobs = new Mock<IJobStore>(MockBehavior.Strict);
        jobs.Setup(s => s.GetRunAsync("lingering", It.IsAny<CancellationToken>())).ReturnsAsync(Run("lingering", JobRunStatus.Succeeded, past));
        jobs.Setup(s => s.GetRunAsync("fresh", It.IsAny<CancellationToken>())).ReturnsAsync(Run("fresh", JobRunStatus.Succeeded));
        jobs.Setup(s => s.GetRunAsync("running", It.IsAny<CancellationToken>())).ReturnsAsync(Run("running", JobRunStatus.Running));
        // An active session has no finished recording to wait for, so the session store is never consulted.
        var sessions = new Mock<ISessionStore>(MockBehavior.Strict);
        using var services = new ServiceCollection().AddSingleton(jobs.Object).AddSingleton(sessions.Object).BuildServiceProvider();
        var requests = new ConcurrentQueue<string>();
        using var http = new StatusHandler(request =>
        {
            requests.Enqueue($"{request.Method} {request.RequestUri!.Port}{request.RequestUri.AbsolutePath}");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"hasActiveSession\":true,\"sessionId\":\"outer\"}") };
        });
        var host = Host(services, http);
        var registry = Registry(host);
        using var placeholder = new Process();
        using var fresh = StartHarmlessProcess();
        using var lingering = StartHarmlessProcess();
        try
        {
            AddChild(registry, "ordinary", placeholder, 0, null);
            AddChild(registry, "running", placeholder, 1, Started("running"));
            AddChild(registry, "fresh", fresh, 2, Started("fresh"));
            AddChild(registry, "lingering", lingering, 3, Started("lingering"));
            await host.CloseCompletedAutomationTabsAsync(TestContext.Current.CancellationToken);
            Assert.False(registry.Contains("lingering"));
            Assert.True(lingering.HasExited);
            Assert.True(registry.Contains("ordinary"));
            Assert.True(registry.Contains("running"));
            Assert.True(registry.Contains("fresh"));
            Assert.False(fresh.HasExited);
            Assert.Equal(["GET 10002/api/v1/terminal/status", "GET 10003/api/v1/terminal/status", "POST 10003/api/v1/terminal/stop"], requests.Order());
            jobs.VerifyAll();
        }
        finally
        {
            registry.Clear();
            await host.DisposeAsync();
            foreach (var process in new[] { fresh, lingering })
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(TestContext.Current.CancellationToken); }
        }
    }

    [Fact]
    public async Task SchedulerClosePassNeverWaitsBehindTabCreation()
    {
        // A Worker launch holds the creation gate through its pre-launch steps; the scheduler's close pass
        // (probes plus graceful stops) must neither wait for it nor make new-tab requests wait for itself.
        using var services = new ServiceCollection().BuildServiceProvider();
        using var http = new StatusHandler(_ => throw new InvalidOperationException("No reclamation probes expected"));
        var host = Host(services, http);
        var gate = (SemaphoreSlim)typeof(TerminalTabHostService)
            .GetField("_createGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(host)!;
        await gate.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await host.CloseCompletedAutomationTabsAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        finally
        {
            gate.Release();
            await host.DisposeAsync();
        }
    }

    [Fact]
    public async Task CapacityCheckLeavesFinishedAutomationForSchedulerWhenThereIsRoom()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var http = new StatusHandler(_ => throw new InvalidOperationException("No reclamation probes expected"));
        var host = Host(services, http);
        var registry = Registry(host);
        using var placeholder = new Process();
        try
        {
            AddChild(registry, "finished", placeholder, 1, Started("finished"));
            await ReclaimIfFullAsync(host);
            Assert.True(registry.Contains("finished"));
        }
        finally
        {
            registry.Clear();
            await host.DisposeAsync();
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

    private static TerminalTabHostService Host(ServiceProvider services, HttpMessageHandler http, IAppEventBus? events = null)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(value => value.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(http, disposeHandler: false));
        return new TerminalTabHostService(factory.Object, Mock.Of<ILocalClientTracker>(), events ?? Mock.Of<IAppEventBus>(),
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
        .Invoke(host, [TestContext.Current.CancellationToken, true])!;

    private static AutomationTabState Started(string id)
    {
        var state = new AutomationTabState(id, id);
        state.BeginSessionStart();
        state.EndSessionStart(new(true, "outer", "shell", "/project"));
        return state;
    }

    private static JobRunRecord Run(string id, JobRunStatus status, DateTime? endedUtc = null) => new(id, 1, JobTriggerKind.Manual, $"manual:{id}",
        status, id, "/project", LLM.NotSet, null, null, null, null, DateTime.UtcNow, DateTime.UtcNow,
        endedUtc ?? DateTime.UtcNow, 0, null, false, null, TerminalSessionId: "outer");

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
