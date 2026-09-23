using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// The cross-repository import routes over a real host: they must exist only on the root
/// backend, and their bodies must bind through the AOT serializer context (a DTO missing from
/// <see cref="AppJsonSerializerContext"/> compiles fine and then 500s on the first request).
/// </summary>
public sealed class JobRoutesTests : IDisposable
{
    private static readonly HttpClient SharedClient = new();

    private readonly string _repoRoot;
    private readonly Mock<IAutomationImportService> _importService = new();
    private readonly Mock<IJobService> _jobService = new();

    public JobRoutesTests()
    {
        _repoRoot = Path.Combine(Path.GetTempPath(), $"vb-jobroutes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repoRoot);
        RunGit("init");
        _repoRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_repoRoot));
    }

    [Fact]
    public async Task ImportRoutesAreNotMappedOnATerminalTabBackend()
    {
        await WithHostAsync(async baseUri =>
        {
            using var catalog = await SharedClient.GetAsync(
                new Uri(baseUri, "/api/v1/jobs/catalog"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, catalog.StatusCode);

            // 405 rather than 404: the HTTP-method policy is baked into the routing DFA ahead of
            // the {id:long} constraint, so "import" reaches the GET/PUT/DELETE /jobs/{id} node and
            // is refused for POST there. Either way nothing on this host serves the import.
            using var import = await PostAsync(baseUri, "/api/v1/jobs/import", new AutomationImportRequest(1));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, import.StatusCode);
        }, isActiveRootBackend: false);

        _importService.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task CatalogRoundTripsThroughTheAotJsonContext()
    {
        var entry = new AutomationImportCatalogEntry(
            42, "Nightly review", @"C:\src\other", "Other project", true, true, DateTime.UtcNow,
            new AutomationImportWorker(
                7, "Nightly", LLM.Claude, "claude", "--model opus", "Review the diff", 1,
                [new EnvironmentStepDto("s1", 2, 0, "Last commit", "git log -1", false, 600, true)],
                null, null, "Nightly - repo"),
            [
                new AutomationImportAction(JobActionKind.Script, "scripts/check.py", JobScriptRuntime.Python,
                    ["--strict"], "scripts", 30, false, true),
                new AutomationImportAction(JobActionKind.Worker, null, null, [], null, null, false, false)
            ],
            45, false,
            [new JobTriggerRequest(JobTriggerKind.Schedule, JobScheduleKind.Daily, null, "02:00", 0, "UTC")],
            true, null);
        _importService
            .Setup(service => service.GetCatalogAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AutomationImportCatalogResponse(
                _repoRoot,
                [new AutomationImportProjectGroup(@"C:\src\other", "Other project", true, [entry])]));

        await WithHostAsync(async baseUri =>
        {
            using var response = await SharedClient.GetAsync(
                new Uri(baseUri, "/api/v1/jobs/catalog"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var catalog = await response.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.AutomationImportCatalogResponse,
                TestContext.Current.CancellationToken);
            var group = Assert.Single(catalog!.Projects);
            Assert.Equal("Other project", group.DisplayName);
            var received = Assert.Single(group.Automations);
            Assert.Equal(42, received.SourceJobId);
            Assert.Equal("Nightly review", received.Name);
            Assert.True(received.CanImport);
            Assert.Null(received.Blocker);
            Assert.Equal(45, received.TimeoutMinutes);
            Assert.Equal("Nightly - repo", received.Worker?.SuggestedCloneName);
            Assert.Equal(LLM.Claude, received.Worker?.Llm);
            Assert.Equal("claude", received.Worker?.Cli);
            Assert.Equal("git log -1", Assert.Single(received.Worker!.Steps).Command);
            Assert.Equal(
                [JobActionKind.Script, JobActionKind.Worker],
                received.Actions.Select(action => action.Kind));
            Assert.Equal(["--strict"], received.Actions[0].Arguments);
            Assert.True(received.Actions[0].ExistsInSourceRepository);
            Assert.False(received.Actions[0].ExistsInTargetRepository);
            var trigger = Assert.Single(received.Triggers);
            Assert.Equal(JobTriggerKind.Schedule, trigger.Kind);
            Assert.Equal("02:00", trigger.LocalTime);
        });
    }

    [Fact]
    public async Task ImportBindsTheRequestAndReturnsTheServiceResponse()
    {
        AutomationImportRequest? bound = null;
        _importService
            .Setup(service => service.ImportAsync(It.IsAny<string>(), It.IsAny<AutomationImportRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, AutomationImportRequest request, CancellationToken _) =>
            {
                bound = request;
                return new AutomationImportResponse(
                    new JobResponse(9, "Nightly review", _repoRoot, LLM.Claude, 500, "Nightly - repo", "Review",
                        45, false, DateTime.UtcNow, DateTime.UtcNow, null, []),
                    AutomationImportWorkerOutcome.Created, "Nightly - repo", ["scripts/check.py"], ["work"]);
            });

        await WithHostAsync(async baseUri =>
        {
            using var response = await PostAsync(
                baseUri, "/api/v1/jobs/import", new AutomationImportRequest(42, "Nightly - repo"));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var result = await response.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.AutomationImportResponse,
                TestContext.Current.CancellationToken);
            Assert.Equal(9, result!.Job.Id);
            Assert.False(result.Job.Enabled);
            Assert.Equal(AutomationImportWorkerOutcome.Created, result.WorkerOutcome);
            Assert.Equal(["scripts/check.py"], result.CopiedScripts);
            Assert.Equal(["work"], result.CreatedDirectories);
        });

        Assert.Equal(42, bound?.SourceJobId);
        Assert.Equal("Nightly - repo", bound?.WorkerName);
    }

    [Fact]
    public async Task ImportReportsAServiceConflictAsA409WithTheMessage()
    {
        _importService
            .Setup(service => service.ImportAsync(It.IsAny<string>(), It.IsAny<AutomationImportRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(JobServiceException.Conflict("An environment named 'Nightly - repo' already exists. Choose a different Worker name."));

        await WithHostAsync(async baseUri =>
        {
            using var response = await PostAsync(baseUri, "/api/v1/jobs/import", new AutomationImportRequest(42));
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

            var error = await response.Content.ReadFromJsonAsync(
                AppJsonSerializerContext.Default.ErrorResponse,
                TestContext.Current.CancellationToken);
            Assert.Contains("Choose a different Worker name", error!.Error);
        });
    }

    [Fact]
    public async Task CreateJobDropsAClientSuppliedImportOrigin()
    {
        // Only ImportAsync may mark an Automation as a copy; a hand-built create that claims an
        // origin would make the catalog hide a real Automation.
        CreateJobRequest? received = null;
        _jobService
            .Setup(service => service.CreateJobAsync(It.IsAny<CreateJobRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((CreateJobRequest request, CancellationToken _) =>
            {
                received = request;
                return new JobResponse(
                    1, request.Name, request.ProjectPath, LLM.NotSet, null, null, string.Empty, null, false,
                    DateTime.UtcNow, DateTime.UtcNow, null, [], false, null, request.ImportedFromJobId);
            });

        await WithHostAsync(async baseUri =>
        {
            var body = new CreateJobRequest(
                "Checks", @"C:\elsewhere", LLM.NotSet, null, string.Empty, null, false, [],
                Actions: [new JobActionRequest(null, JobActionKind.Script, null, "scripts/check.py", JobScriptRuntime.Python)],
                ImportedFromJobId: 9);
            using var response = await SharedClient.PostAsync(
                new Uri(baseUri, "/api/v1/jobs"),
                JsonContent.Create(body, AppJsonSerializerContext.Default.CreateJobRequest),
                TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        });

        Assert.NotNull(received);
        Assert.Null(received!.ImportedFromJobId);
        Assert.Equal("Checks", received.Name);
    }

    private static Task<HttpResponseMessage> PostAsync(Uri baseUri, string path, AutomationImportRequest body) =>
        SharedClient.PostAsync(
            new Uri(baseUri, path),
            JsonContent.Create(body, AppJsonSerializerContext.Default.AutomationImportRequest),
            TestContext.Current.CancellationToken);

    private async Task WithHostAsync(Func<Uri, Task> test, bool isActiveRootBackend = true)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(_importService.Object);
        builder.Services.AddSingleton(_jobService.Object);
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default);
        });

        await using var app = builder.Build();
        JobRoutes.Map(app, _repoRoot, isActiveRootBackend);
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

    private void RunGit(string arguments)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = _repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        process.WaitForExit();
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); }
        catch { /* a leftover temp repo is not worth failing a test run over */ }
    }
}
