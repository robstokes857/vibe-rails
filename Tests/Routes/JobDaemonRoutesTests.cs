using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.Jobs;
using Xunit;

namespace Tests.Routes;

public sealed class JobDaemonRoutesTests
{
    [Fact]
    public async Task LifecycleRoutes_MapTheExactStatusAndActionSurface()
    {
        var service = new RecordingLifecycleService();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton<IJobDaemonLifecycleService>(service);
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));

        await using var app = builder.Build();
        JobDaemonRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
            var requests = new[]
            {
                new HttpRequestMessage(HttpMethod.Get, "/api/v1/jobs/demon"),
                new HttpRequestMessage(HttpMethod.Post, "/api/v1/jobs/demon/install"),
                new HttpRequestMessage(HttpMethod.Post, "/api/v1/jobs/demon/start"),
                new HttpRequestMessage(HttpMethod.Post, "/api/v1/jobs/demon/stop"),
                new HttpRequestMessage(HttpMethod.Post, "/api/v1/jobs/demon/restart"),
                new HttpRequestMessage(HttpMethod.Post, "/api/v1/jobs/demon/repair"),
                new HttpRequestMessage(HttpMethod.Delete, "/api/v1/jobs/demon")
            };

            foreach (var request in requests)
            {
                using (request)
                using (var response = await client.SendAsync(request, TestContext.Current.CancellationToken))
                    Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            Assert.Equal(
                ["status", "install", "start", "stop", "restart", "repair", "remove"],
                service.Calls);
        }
        finally
        {
            await app.StopAsync(CancellationToken.None);
        }
    }

    private sealed class RecordingLifecycleService : IJobDaemonLifecycleService
    {
        private static readonly JobDaemonStatusResponse Status = new(
            JobDaemonState.NotInstalled,
            "test",
            true,
            false,
            false,
            false,
            false,
            "1.0.0",
            AllowedActions: ["install"]);

        public List<string> Calls { get; } = [];

        public Task<JobDaemonStatusResponse> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("status");
            return Task.FromResult(Status);
        }

        public Task<JobDaemonActionResponse> InstallAsync(CancellationToken cancellationToken = default) =>
            Action("install");

        public Task<JobDaemonActionResponse> StartAsync(CancellationToken cancellationToken = default) =>
            Action("start");

        public Task<JobDaemonActionResponse> StopAsync(CancellationToken cancellationToken = default) =>
            Action("stop");

        public Task<JobDaemonActionResponse> RestartAsync(CancellationToken cancellationToken = default) =>
            Action("restart");

        public Task<JobDaemonActionResponse> RepairAsync(CancellationToken cancellationToken = default) =>
            Action("repair");

        public Task<JobDaemonActionResponse> UninstallAsync(CancellationToken cancellationToken = default) =>
            Action("remove");

        private Task<JobDaemonActionResponse> Action(string action)
        {
            Calls.Add(action);
            return Task.FromResult(new JobDaemonActionResponse(true, action, Status));
        }
    }
}
