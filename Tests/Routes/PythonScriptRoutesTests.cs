using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
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
            Assert.False(File.Exists(ScriptPath("nightly.py")));
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
            Assert.Contains(".py file name", body!.Error);
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
            Assert.Equal("copied.py", Assert.Single(list!.Scripts).Name);
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
    public async Task RunCarriesArgumentsAndStandardInputThroughTheAotJsonContext()
    {
        // Arguments and stdin are new fields on an existing request record: they bind
        // through the source-generated context, so a missing registration would only
        // show up here, at runtime, as a silently empty argv.
        var scripts = new Mock<IPythonScriptService>(MockBehavior.Loose);
        IReadOnlyList<string>? forwardedArguments = null;
        string? forwardedStandardInput = null;
        scripts
            .Setup(service => service.RunAsync(
                It.IsAny<string?>(),
                It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .Callback(new InvocationAction(invocation =>
            {
                forwardedArguments = (IReadOnlyList<string>?)invocation.Arguments[1];
                forwardedStandardInput = (string?)invocation.Arguments[2];
            }))
            .ReturnsAsync(new PythonScriptRunResponse(
                "report.py", 0, false, "{\"rows\": 3}", "", 12, DateTime.UtcNow.ToString("O"),
                "{\"rows\": 3}"));

        await WithHostAsync(async baseUri =>
        {
            using var response = await PostAsync(
                baseUri,
                "/api/v1/python-scripts/run",
                new PythonScriptRunRequest("report.py", ["--out", "report.csv", "50"], "piped text"),
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
