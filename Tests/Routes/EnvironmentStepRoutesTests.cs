using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Services.Cli;
using VibeRails.Services.Workspaces;
using Xunit;

namespace Tests.Routes;

public sealed class EnvironmentStepRoutesTests
{
    // One client for the whole class — per-test HttpClients leave TIME_WAIT sockets behind and
    // can destabilize the machine running the suite.
    private static readonly HttpClient SharedClient = new();

    // ---------------------------------------------------------------------------------------
    //  Validation (TryBuildSteps is what both POST and PUT route their step lists through)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void Steps_Null_MeansLeaveThemUntouched()
    {
        Assert.True(EnvironmentStepRoutes.TryBuildSteps(null, out var steps, out var error));
        Assert.Empty(steps);
        Assert.Null(error);
    }

    [Fact]
    public void Steps_EmptyList_IsAnExplicitClear()
    {
        // Distinct from null: an editor that was opened and emptied must be able to say so.
        Assert.True(EnvironmentStepRoutes.TryBuildSteps([], out var steps, out var error));
        Assert.Empty(steps);
        Assert.Null(error);
    }

    [Fact]
    public void Steps_RejectAnUnknownPhase()
    {
        var requests = new List<EnvironmentStepRequest> { new(Phase: 7, "bad", "git pull") };

        Assert.False(EnvironmentStepRoutes.TryBuildSteps(requests, out _, out var error));
        Assert.Contains("Unknown step phase", error);
    }

    [Fact]
    public void Steps_RejectABlankCommand()
    {
        var requests = new List<EnvironmentStepRequest> { new(0, "empty", "   ") };

        Assert.False(EnvironmentStepRoutes.TryBuildSteps(requests, out _, out var error));
        Assert.Equal("A step needs a command.", error);
    }

    [Fact]
    public void Steps_RejectAnOverlongCommand()
    {
        var requests = new List<EnvironmentStepRequest>
        {
            new(0, "huge", new string('x', EnvironmentStepRoutes.MaxCommandChars + 1))
        };

        Assert.False(EnvironmentStepRoutes.TryBuildSteps(requests, out _, out var error));
        Assert.Contains("exceeds", error);
    }

    [Fact]
    public void Steps_RejectControlCharactersButAllowNewlinesAndTabs()
    {
        var multiline = new List<EnvironmentStepRequest> { new(0, "ok", "git pull\r\n\tnpm ci\n") };
        Assert.True(EnvironmentStepRoutes.TryBuildSteps(multiline, out _, out _));

        var withNul = new List<EnvironmentStepRequest> { new(0, "bad", "git pull\0rm -rf /") };
        Assert.False(EnvironmentStepRoutes.TryBuildSteps(withNul, out _, out var error));
        Assert.Contains("control characters", error);
    }

    [Fact]
    public void Steps_RejectMoreThanTheMaximum()
    {
        var requests = Enumerable
            .Range(0, EnvironmentStepRoutes.MaxStepsPerEnvironment + 1)
            .Select(index => new EnvironmentStepRequest(0, $"step {index}", "git pull"))
            .ToList();

        Assert.False(EnvironmentStepRoutes.TryBuildSteps(requests, out _, out var error));
        Assert.Contains("at most", error);
    }

    [Theory]
    [InlineData(0, EnvironmentStep.MinTimeoutSeconds)]
    [InlineData(-1, EnvironmentStep.MinTimeoutSeconds)]
    [InlineData(10_000, EnvironmentStep.MaxTimeoutSeconds)]
    [InlineData(900, 900)]
    public void Steps_ClampTheTimeoutRatherThanRejectIt(int requested, int expected)
    {
        var requests = new List<EnvironmentStepRequest>
        {
            new(0, "install", "npm ci", TimeoutSeconds: requested)
        };

        Assert.True(EnvironmentStepRoutes.TryBuildSteps(requests, out var steps, out _));
        Assert.Equal(expected, Assert.Single(steps).TimeoutSeconds);
    }

    [Fact]
    public void Steps_KeepAClientSuppliedGuidAndGenerateOneWhenMissing()
    {
        // The id is what {{step:<id>}} prompt references point at, so a valid client GUID must
        // survive the build verbatim (normalized to Guid's canonical form).
        var supplied = Guid.NewGuid().ToString().ToUpperInvariant();
        var requests = new List<EnvironmentStepRequest>
        {
            new(0, "keep", "git pull", Id: supplied),
            new(0, "generate", "npm ci"),
            new(0, "garbage", "dotnet build", Id: "not-a-guid")
        };

        Assert.True(EnvironmentStepRoutes.TryBuildSteps(requests, out var steps, out _));
        Assert.Equal(supplied.ToLowerInvariant(), steps[0].Id);
        Assert.True(Guid.TryParse(steps[1].Id, out _));
        Assert.True(Guid.TryParse(steps[2].Id, out _));
        Assert.NotEqual(steps[1].Id, steps[2].Id);
    }

    [Fact]
    public void Steps_RegenerateADuplicatedIdRatherThanRejectTheSave()
    {
        var shared = Guid.NewGuid().ToString();
        var requests = new List<EnvironmentStepRequest>
        {
            new(0, "first", "git pull", Id: shared),
            new(0, "second", "npm ci", Id: shared)
        };

        Assert.True(EnvironmentStepRoutes.TryBuildSteps(requests, out var steps, out _));
        Assert.Equal(shared, steps[0].Id);
        Assert.NotEqual(shared, steps[1].Id);
        Assert.True(Guid.TryParse(steps[1].Id, out _));
    }

    [Fact]
    public void Steps_AcceptTheManualPhase()
    {
        // Phase 2 = "only when referenced" from the Initial Message; it must save like any other.
        var requests = new List<EnvironmentStepRequest> { new(2, "capture", "git log -1") };

        Assert.True(EnvironmentStepRoutes.TryBuildSteps(requests, out var steps, out _));
        Assert.Equal(EnvironmentStepPhase.Manual, Assert.Single(steps).Phase);
    }

    [Fact]
    public void Steps_TrimTheNameButPreserveTheCommandVerbatim()
    {
        // The command is a shell script: leading whitespace can be significant, so only the label
        // is normalized.
        var requests = new List<EnvironmentStepRequest> { new(1, "  Push  ", "  git push  ") };

        Assert.True(EnvironmentStepRoutes.TryBuildSteps(requests, out var steps, out _));
        var step = Assert.Single(steps);
        Assert.Equal("Push", step.Name);
        Assert.Equal("  git push  ", step.Command);
        Assert.Equal(EnvironmentStepPhase.PostExit, step.Phase);
    }

    // ---------------------------------------------------------------------------------------
    //  PUT /api/v1/environments/{name}
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Put_WithSteps_ReplacesThemAndReturnsThemOnTheResponse()
    {
        var environment = NewEnvironment();
        var repository = NewRepositoryMock(environment);
        List<EnvironmentStep>? replaced = null;
        repository
            .Setup(item => item.ReplaceStepsAsync(
                environment.Id, It.IsAny<IReadOnlyList<EnvironmentStep>>(), It.IsAny<CancellationToken>()))
            .Callback((int _, IReadOnlyList<EnvironmentStep> steps, CancellationToken _) => replaced = steps.ToList())
            .Returns(Task.CompletedTask);
        repository
            .Setup(item => item.GetStepsForEnvironmentAsync(environment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => (replaced ?? []).Select((step, index) =>
            {
                step.Position = index;
                return step;
            }).ToList());

        await WithEnvironmentHostAsync(repository, async baseUrl =>
        {
            using var response = await SharedClient.PutAsJsonAsync(
                $"{baseUrl}/api/v1/environments/review",
                new UpdateEnvironmentRequest("review", Steps:
                [
                    new EnvironmentStepRequest(0, "Pull", "git pull"),
                    new EnvironmentStepRequest(1, "Push", "git push", StartMinimized: true, TimeoutSeconds: 120)
                ]),
                TestContext.Current.CancellationToken);

            var body = await response.Content.ReadFromJsonAsync<EnvironmentResponse>(
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.NotNull(replaced);
            Assert.Equal(["Pull", "Push"], replaced!.Select(step => step.Name));
            Assert.Equal(2, body!.Steps!.Count);
            Assert.Equal((int)EnvironmentStepPhase.PostExit, body.Steps[1].Phase);
            Assert.True(body.Steps[1].StartMinimized);
            Assert.Equal(120, body.Steps[1].TimeoutSeconds);
        });
    }

    [Fact]
    public async Task Put_WithoutSteps_LeavesTheStoredListUntouched()
    {
        // The steps editor is a separate modal; an environment form saved without opening it must
        // not silently wipe a configured setup chain.
        var environment = NewEnvironment();
        var repository = NewRepositoryMock(environment);
        repository
            .Setup(item => item.GetStepsForEnvironmentAsync(environment.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new EnvironmentStep { Id = Guid.NewGuid().ToString(), EnvironmentId = environment.Id, Name = "Pull", Command = "git pull" }]);

        await WithEnvironmentHostAsync(repository, async baseUrl =>
        {
            using var response = await SharedClient.PutAsJsonAsync(
                $"{baseUrl}/api/v1/environments/review",
                new UpdateEnvironmentRequest("review", CustomArgs: "--yolo"),
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            repository.Verify(
                item => item.ReplaceStepsAsync(
                    It.IsAny<int>(), It.IsAny<IReadOnlyList<EnvironmentStep>>(), It.IsAny<CancellationToken>()),
                Times.Never);
        });
    }

    [Fact]
    public async Task Put_WithAnInvalidStep_Is400AndWritesNothing()
    {
        var environment = NewEnvironment();
        var repository = NewRepositoryMock(environment);

        await WithEnvironmentHostAsync(repository, async baseUrl =>
        {
            using var response = await SharedClient.PutAsJsonAsync(
                $"{baseUrl}/api/v1/environments/review",
                new UpdateEnvironmentRequest("review", Steps: [new EnvironmentStepRequest(0, "bad", "")]),
                TestContext.Current.CancellationToken);
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("A step needs a command.", error!.Error);
            // Validation runs before any write, so a bad list rejects the whole update rather
            // than half-applying it.
            repository.Verify(
                item => item.UpdateEnvironmentAsync(It.IsAny<LLM_Environment>(), It.IsAny<CancellationToken>()),
                Times.Never);
            repository.Verify(
                item => item.ReplaceStepsAsync(
                    It.IsAny<int>(), It.IsAny<IReadOnlyList<EnvironmentStep>>(), It.IsAny<CancellationToken>()),
                Times.Never);
        });
    }

    // ---------------------------------------------------------------------------------------
    //  POST /api/v1/environments/steps/test  (SSE)
    // ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Test_StreamsOutputLinesThenATerminalDoneEvent()
    {
        await WithTestEndpointAsync(async baseUrl =>
        {
            var events = await ReadTestStreamAsync(
                baseUrl,
                new TestEnvironmentStepRequest("echo viberails-step-marker", Path.GetTempPath()));

            Assert.Contains(events, e => e.Type == "line" && (e.Line ?? "").Contains("viberails-step-marker"));
            var done = Assert.Single(events, e => e.Type == "done");
            Assert.Equal(0, done.ExitCode);
            Assert.NotNull(done.DurationMs);
        });
    }

    [Fact]
    public async Task Test_ReportsANonZeroExitWithoutBreakingTheStream()
    {
        await WithTestEndpointAsync(async baseUrl =>
        {
            var events = await ReadTestStreamAsync(
                baseUrl,
                new TestEnvironmentStepRequest("exit 3", Path.GetTempPath()));

            var done = Assert.Single(events, e => e.Type == "done");
            Assert.Equal(3, done.ExitCode);
        });
    }

    [Fact]
    public async Task Test_RejectsABlankCommandWithA400RatherThanAnEmptyStream()
    {
        await WithTestEndpointAsync(async baseUrl =>
        {
            using var response = await SharedClient.PostAsJsonAsync(
                $"{baseUrl}/api/v1/environments/steps/test",
                new TestEnvironmentStepRequest("   "),
                TestContext.Current.CancellationToken);
            var error = await response.Content.ReadFromJsonAsync<ErrorResponse>(
                TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("A step needs a command.", error!.Error);
        });
    }

    // ---------------------------------------------------------------------------------------

    private static LLM_Environment NewEnvironment() => new()
    {
        Id = 91,
        CustomName = "review",
        LLM = LLM.Codex,
        Path = "unused"
    };

    private static Mock<IRepository> NewRepositoryMock(LLM_Environment environment)
    {
        var repository = new Mock<IRepository>();
        repository
            .Setup(item => item.FindEnvironmentByNameAsync("review", It.IsAny<CancellationToken>()))
            .ReturnsAsync(environment);
        repository
            .Setup(item => item.UpdateEnvironmentAsync(It.IsAny<LLM_Environment>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repository
            .Setup(item => item.GetStepsForEnvironmentAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        return repository;
    }

    private static async Task WithEnvironmentHostAsync(Mock<IRepository> repository, Func<string, Task> test)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(repository.Object);
        builder.Services.AddSingleton(new Mock<IJobStore>().Object);
        builder.Services.AddSingleton(new Mock<IRunWorkspaceService>().Object);

        await using var app = builder.Build();
        EnvironmentRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await test(app.Urls.First());
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static async Task WithTestEndpointAsync(Func<string, Task> test)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        // The real wrapper: the point of this endpoint is that it runs the command the same way a
        // launch would, so faking the runner would test nothing worth testing.
        builder.Services.AddSingleton<ICliWrapper, CliWrapper>();

        await using var app = builder.Build();
        EnvironmentStepRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await test(app.Urls.First());
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<List<EnvironmentStepTestEvent>> ReadTestStreamAsync(
        string baseUrl,
        TestEnvironmentStepRequest request)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var message = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl}/api/v1/environments/steps/test")
        {
            Content = JsonContent.Create(request)
        };

        using var response = await SharedClient.SendAsync(
            message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);

        var events = new List<EnvironmentStepTestEvent>();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            var parsed = System.Text.Json.JsonSerializer.Deserialize<EnvironmentStepTestEvent>(
                line["data: ".Length..],
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            if (parsed != null) events.Add(parsed);
        }

        return events;
    }
}
