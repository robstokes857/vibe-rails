using System.Net;
using System.Net.Http.Json;
using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PyBridge;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.PythonScripts;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// The script-authoring endpoints over a real host. These bodies bind through the AOT
/// serializer context, which is a runtime-only failure mode: a DTO missing from
/// <see cref="AppJsonSerializerContext"/> compiles fine and then 500s on the first POST.
/// </summary>
public sealed class PythonScriptRoutesTests : IDisposable
{
    private static readonly HttpClient SharedClient = new();

    private readonly string _installDirectory = Path.Combine(
        Path.GetTempPath(), "vb-pyscript-routes", Guid.NewGuid().ToString("N"));

    private string ScriptPath(string name) =>
        Path.Combine(_installDirectory, PythonScriptService.ScriptsSubdirectory, name);

    public void Dispose()
    {
        try { Directory.Delete(_installDirectory, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public async Task RegistrationSettingsBindThroughTheAotContextAndKeepTheOriginalFile()
    {
        Directory.CreateDirectory(_installDirectory);
        var path = Path.Combine(_installDirectory, "launch.ps1");
        File.WriteAllText(path, "Write-Output 1");
        await WithHostAsync(async baseUri =>
        {
            using var added = await PostAsync(baseUri, "/api/v1/python-scripts/import",
                new PythonScriptImportRequest(path, DisplayName: "Launch app", RequirePinEachRun: true),
                AppJsonSerializerContext.Default.PythonScriptImportRequest);
            Assert.Equal(HttpStatusCode.OK, added.StatusCode);
            var list = await added.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.PythonScriptListResponse,
                TestContext.Current.CancellationToken);
            var script = Assert.Single(list!.Scripts);
            Assert.Equal(path, script.Path);
            Assert.Equal("Launch app", script.DisplayName);
            Assert.True(script.RequirePinEachRun);
            using var updated = await PostAsync(baseUri, "/api/v1/python-scripts/settings",
                new PythonScriptSettingsRequest(script.Id!, "Start app", "global", true),
                AppJsonSerializerContext.Default.PythonScriptSettingsRequest);
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            var saved = await updated.Content.ReadFromJsonAsync(AppJsonSerializerContext.Default.PythonScriptListResponse,
                TestContext.Current.CancellationToken);
            Assert.Equal("Start app", Assert.Single(saved!.Scripts).DisplayName);
            Assert.Equal("Write-Output 1", File.ReadAllText(path));
        });
    }

    [Fact]
    public async Task CreateThenReadRoundTripsTheScriptThroughTheAotJsonContext()
    {
        await WithHostAsync(async baseUri =>
        {
            using var created = await PostAsync(
                baseUri, "/api/v1/python-scripts/create",
                new PythonScriptSaveRequest("hello.py", "print('hello')\n"),
                AppJsonSerializerContext.Default.PythonScriptSaveRequest);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);

            var list = await created.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.PythonScriptListResponse,
                TestContext.Current.CancellationToken);
            var script = Assert.Single(list!.Scripts);
            Assert.Equal("hello.py", script.Name);
            Assert.Equal(PythonScriptService.StatusUnapproved, script.Status);
            Assert.Equal(ScriptPath("hello.py"), script.Path);

            using var read = await SharedClient.GetAsync(
                new Uri(baseUri, "/api/v1/python-scripts/content?name=hello.py"),
                TestContext.Current.CancellationToken);
            var content = await read.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.PythonScriptContentResponse,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
            Assert.Equal("print('hello')\n", content!.Content);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("job.py")]
    public async Task RunPinRequirementRoundTripsAndRejectsIncorrectPin(string? name)
    {
        await WithHostAsync(async baseUri =>
        {
            using var created = await PostAsync(
                baseUri, "/api/v1/python-scripts/create",
                new PythonScriptSaveRequest("job.py", "print(1)\n"),
                AppJsonSerializerContext.Default.PythonScriptSaveRequest);
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
            using var configured = await PostAsync(
                baseUri, "/api/v1/python-scripts/pin",
                new SetPythonScriptPinRequest(null, "1234"),
                AppJsonSerializerContext.Default.SetPythonScriptPinRequest);
            Assert.Equal(HttpStatusCode.OK, configured.StatusCode);

            using var enabled = await PostAsync(
                baseUri, "/api/v1/python-scripts/run-pin",
                new PythonScriptRunPinRequirementRequest(name, true, "1234"),
                AppJsonSerializerContext.Default.PythonScriptRunPinRequirementRequest);
            Assert.Equal(HttpStatusCode.OK, enabled.StatusCode);
            var state = await enabled.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.PythonScriptListResponse,
                TestContext.Current.CancellationToken);
            Assert.Equal(name is null, state!.RequirePinEachRun);
            Assert.Equal(name is not null, Assert.Single(state.Scripts).RequirePinEachRun);

            using var rejected = await PostAsync(
                baseUri, "/api/v1/python-scripts/run-pin",
                new PythonScriptRunPinRequirementRequest(name, false, "9999"),
                AppJsonSerializerContext.Default.PythonScriptRunPinRequirementRequest);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            using var saved = await SharedClient.GetAsync(
                new Uri(baseUri, "/api/v1/python-scripts"), TestContext.Current.CancellationToken);
            var savedState = await saved.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.PythonScriptListResponse,
                TestContext.Current.CancellationToken);
            Assert.Equal(state.RequirePinEachRun, savedState!.RequirePinEachRun);
            Assert.Equal(Assert.Single(state.Scripts).RequirePinEachRun,
                Assert.Single(savedState.Scripts).RequirePinEachRun);

            using var disabled = await PostAsync(
                baseUri, "/api/v1/python-scripts/run-pin",
                new PythonScriptRunPinRequirementRequest(name, false, "1234"),
                AppJsonSerializerContext.Default.PythonScriptRunPinRequirementRequest);
            Assert.Equal(HttpStatusCode.OK, disabled.StatusCode);
            var disabledState = await disabled.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.PythonScriptListResponse,
                TestContext.Current.CancellationToken);
            Assert.False(disabledState!.RequirePinEachRun);
            Assert.False(Assert.Single(disabledState.Scripts).RequirePinEachRun);
        });
    }

    [Fact]
    public async Task SaveRenameAndDeleteMoveTheFileAndReportTheNewList()
    {
        await WithHostAsync(async baseUri =>
        {
            using (await PostAsync(
                baseUri, "/api/v1/python-scripts/create",
                new PythonScriptSaveRequest("job.py", "print(1)\n"),
                AppJsonSerializerContext.Default.PythonScriptSaveRequest)) { }

            using var openedResponse = await SharedClient.GetAsync(
                new Uri(baseUri, "/api/v1/python-scripts/content?name=job.py"),
                TestContext.Current.CancellationToken);
            var opened = await openedResponse.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.PythonScriptContentResponse,
                TestContext.Current.CancellationToken);

            using (var saved = await PostAsync(
                baseUri, "/api/v1/python-scripts/content",
                new PythonScriptSaveRequest("job.py", "print(2)\n", opened!.Version),
                AppJsonSerializerContext.Default.PythonScriptSaveRequest))
            {
                Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
                var result = await saved.Content.ReadFromJsonAsync(
                    AppJsonSerializerContext.Default.PythonScriptSaveResponse,
                    TestContext.Current.CancellationToken);
                Assert.NotEqual(opened.Version, result!.Version);
            }

            Assert.Equal("print(2)\n", File.ReadAllText(ScriptPath("job.py")));

            using (var renamed = await PostAsync(
                baseUri, "/api/v1/python-scripts/rename",
                new PythonScriptRenameRequest("job.py", "nightly.py"),
                AppJsonSerializerContext.Default.PythonScriptRenameRequest))
            {
                var list = await renamed.Content.ReadFromJsonAsync(
                    AppJsonSerializerContext.Default.PythonScriptListResponse,
                    TestContext.Current.CancellationToken);
                Assert.Equal("nightly.py", Assert.Single(list!.Scripts).Name);
            }

            using var deleted = await SharedClient.DeleteAsync(
                new Uri(baseUri, "/api/v1/python-scripts?name=nightly.py"),
                TestContext.Current.CancellationToken);
            var remaining = await deleted.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.PythonScriptListResponse,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
            Assert.Empty(remaining!.Scripts);
            Assert.True(File.Exists(ScriptPath("nightly.py")));
        });
    }

    [Fact]
    public async Task ARejectedNameIsA400WithTheServiceMessage()
    {
        await WithHostAsync(async baseUri =>
        {
            using var response = await PostAsync(
                baseUri, "/api/v1/python-scripts/create",
                new PythonScriptSaveRequest("../escape.py", "print(1)\n"),
                AppJsonSerializerContext.Default.PythonScriptSaveRequest);

            var body = await response.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.ErrorResponse,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("plain .py, .ps1 or .sh file name", body!.Error);
            Assert.DoesNotContain(nameof(PythonScriptValidationException), body.Error, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ImportIsMappedOnlyOnTheRootDashboardBackend()
    {
        Directory.CreateDirectory(_installDirectory);
        var source = Path.Combine(_installDirectory, "outside.py");
        File.WriteAllText(source, "print('imported')\n");

        await WithHostAsync(async baseUri =>
        {
            using var response = await PostAsync(
                baseUri, "/api/v1/python-scripts/import",
                new PythonScriptImportRequest(source, "copied.py"),
                AppJsonSerializerContext.Default.PythonScriptImportRequest);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }, isActiveRootBackend: false);

        await WithHostAsync(async baseUri =>
        {
            using var response = await PostAsync(
                baseUri, "/api/v1/python-scripts/import",
                new PythonScriptImportRequest(source, "copied.py"),
                AppJsonSerializerContext.Default.PythonScriptImportRequest);
            var list = await response.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.PythonScriptListResponse,
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("outside.py", Assert.Single(list!.Scripts).Name);
            Assert.Equal(source, Assert.Single(list.Scripts).Path);
        });
    }

    [Fact]
    public async Task InteractiveRunCreatesATerminalTabAndReturnsItsIdOnlyOnTheRootBackend()
    {
        var tabHost = new Mock<ITerminalTabHostService>(MockBehavior.Strict);
        tabHost
            .Setup(host => host.CreatePythonScriptTabAsync(
                "prompt.py",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TerminalTabStatusResponse(
                "python-tab",
                DateTime.UtcNow,
                true,
                "session-1",
                "shell",
                ScriptPath(".")));

        await WithHostAsync(async baseUri =>
        {
            using (await PostAsync(baseUri, "/api/v1/python-scripts/create",
                new PythonScriptSaveRequest("prompt.py", "print(1)"), AppJsonSerializerContext.Default.PythonScriptSaveRequest)) { }

            using var response = await PostAsync(
                baseUri,
                "/api/v1/python-scripts/run/interactive",
                new PythonScriptRunRequest("prompt.py"),
                AppJsonSerializerContext.Default.PythonScriptRunRequest);
            var body = await response.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.PythonScriptInteractiveRunResponse,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("prompt.py", body!.Name);
            Assert.Equal("python-tab", body.TabId);
        }, tabHost: tabHost.Object);

        tabHost.VerifyAll();

        await WithHostAsync(async baseUri =>
        {
            using var response = await PostAsync(
                baseUri,
                "/api/v1/python-scripts/run/interactive",
                new PythonScriptRunRequest("prompt.py"),
                AppJsonSerializerContext.Default.PythonScriptRunRequest);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }, isActiveRootBackend: false);
    }

    [Fact]
    public async Task RunCarriesArgumentsStandardInputAndPinThroughTheAotJsonContext()
    {
        // Exercise request binding through the source-generated context so argv,
        // stdin and the run PIN cannot silently disappear during serialization.
        var scripts = new Mock<IPythonScriptService>(MockBehavior.Loose);
        IReadOnlyList<string>? forwardedArguments = null;
        string? forwardedStandardInput = null;
        string? forwardedPin = null;
        scripts
            .Setup(service => service.RunAsync(
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(invocation =>
            {
                forwardedArguments = (IReadOnlyList<string>?)invocation.Arguments[1];
                forwardedStandardInput = (string?)invocation.Arguments[2];
                forwardedPin = (string?)invocation.Arguments[3];
            }))
            .ReturnsAsync(new PythonScriptRunResponse(
                "report.py", 0, false, "{\"rows\": 3}", "", 12, DateTime.UtcNow.ToString("O"),
                "{\"rows\": 3}"));

        await WithHostAsync(async baseUri =>
        {
            using var response = await PostAsync(
                baseUri,
                "/api/v1/python-scripts/run",
                new PythonScriptRunRequest("report.py", ["--out", "report.csv", "50"], "piped text", "1234"),
                AppJsonSerializerContext.Default.PythonScriptRunRequest);
            var body = await response.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.PythonScriptRunResponse,
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            // The return value survives the round trip as its own field, not just as stdout.
            Assert.Equal("{\"rows\": 3}", body!.ReturnJson);
        }, pythonScripts: scripts.Object);

        Assert.Equal(new[] { "--out", "report.csv", "50" }, forwardedArguments);
        Assert.Equal("piped text", forwardedStandardInput);
        Assert.Equal("1234", forwardedPin);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    public async Task RemovedMcpConfigurationRoutes_ReturnNotFound(string method)
    {
        await WithHostAsync(async baseUri =>
        {
            using var request = new HttpRequestMessage(new HttpMethod(method),
                new Uri(baseUri, "/api/v1/python-scripts/mcp"));
            using var response = await SharedClient.SendAsync(request, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        });
    }

    [Fact]
    public async Task CancellingCapturedRunRequestStopsTheProcessTreeAndRetainsTheVerifiedCache()
    {
        var interpreter = PythonLocator.FindBest();
        Assert.SkipWhen(interpreter is null, "Python is not installed on this machine.");
        var service = new PythonScriptService(new PythonRunner(interpreter!.ToOptions()), _installDirectory);
        await service.CreateAsync(new PythonScriptSaveRequest("wait.py", """
            import os, pathlib, subprocess, sys, time
            child = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(120)'])
            marker = pathlib.Path('running.tmp')
            marker.write_text(f'{os.getpid()}\n{child.pid}\n{__file__}', encoding='utf-8')
            os.replace(marker, 'running.txt')
            time.sleep(120)
            """), TestContext.Current.CancellationToken);
        await service.SetPinAsync(new SetPythonScriptPinRequest(null, "1234"), TestContext.Current.CancellationToken);
        await service.ApproveAsync(new PythonScriptApprovalRequest("wait.py", "1234"), TestContext.Current.CancellationToken);

        await WithHostAsync(async baseUri =>
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var request = SharedClient.PostAsync(new Uri(baseUri, "/api/v1/python-scripts/run"),
                JsonContent.Create(new PythonScriptRunRequest("wait.py"), AppJsonSerializerContext.Default.PythonScriptRunRequest),
                cancellation.Token);
            Process? parent = null;
            Process? child = null;
            try
            {
                var marker = ScriptPath("running.txt");
                await WaitUntilAsync(() => File.Exists(marker) || request.IsCompleted);
                Assert.False(request.IsCompleted, "The script should still be running when Stop is pressed.");
                var lines = await File.ReadAllLinesAsync(marker, TestContext.Current.CancellationToken);
                parent = Process.GetProcessById(int.Parse(lines[0]));
                child = Process.GetProcessById(int.Parse(lines[1]));
                var verifiedCopy = lines[2];
                Assert.Equal(ScriptPath(".vb-scripts"), Path.GetDirectoryName(verifiedCopy));
                Assert.True(File.Exists(verifiedCopy));
                Assert.False(parent.HasExited);
                Assert.False(child.HasExited);

                cancellation.Cancel(); // The browser's AbortController disconnects this same request.
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                {
                    using var response = await request;
                });
                await Task.WhenAll(parent.WaitForExitAsync(TestContext.Current.CancellationToken),
                        child.WaitForExitAsync(TestContext.Current.CancellationToken))
                    .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                Assert.True(File.Exists(verifiedCopy), "Stopped runs retain the signed cache for reuse.");
                Assert.True(File.Exists(ScriptPath("wait.py")), "The original script must be retained.");
            }
            finally
            {
                cancellation.Cancel();
                foreach (var process in new[] { parent, child })
                {
                    if (process is null) continue;
                    try
                    {
                        if (!process.HasExited) process.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException) { }
                    finally { process.Dispose(); }
                }
                try { using var response = await request; }
                catch (OperationCanceledException) { }
            }
        }, pythonScripts: service);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }

    private static Task<HttpResponseMessage> PostAsync<TRequest>(
        Uri baseUri,
        string path,
        TRequest request,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<TRequest> typeInfo) =>
        SharedClient.PostAsync(
            new Uri(baseUri, path),
            JsonContent.Create(request, typeInfo),
            TestContext.Current.CancellationToken);

    private async Task WithHostAsync(
        Func<Uri, Task> test,
        bool isActiveRootBackend = true,
        ITerminalTabHostService? tabHost = null,
        IPythonScriptService? pythonScripts = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(pythonScripts
            ?? new PythonScriptService(pythonRunner: null, installDirectory: _installDirectory));
        if (tabHost is not null)
            builder.Services.AddSingleton(tabHost);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
        });

        await using var app = builder.Build();
        PythonScriptRoutes.Map(app, isActiveRootBackend);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await test(new Uri(app.Urls.First()));
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }
}
