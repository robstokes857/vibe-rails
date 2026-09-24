using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.Auth;
using VibeRails.DTOs;
using VibeRails.Middleware;
using VibeRails.Routes;
using VibeRails.Services;
using VibeRails.Services.CodeReports;
using Xunit;

namespace Tests.Routes;

public sealed class CodeGraphRoutesTests
{
    // One client per class: a client per test leaves sockets in TIME_WAIT across the suite.
    private static readonly HttpClient SharedClient = new();

    [Fact]
    public async Task Graph_RequiresBothCredentials_BindsAotJson_AndRejectsUnsafeInput()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var git = new Mock<IGitService>();
        git.Setup(service => service.GetRootPathAsync(It.IsAny<CancellationToken>())).ReturnsAsync("");
        builder.Services.AddSingleton(git.Object);
        builder.Services.AddSingleton<RepositoryCodeGraph>();
        var auth = new Mock<IAuthService>();
        auth.Setup(service => service.ValidateToken(It.IsAny<string?>())).Returns((string? token) => token == "test-session");
        auth.Setup(service => service.ValidateTabToken(It.IsAny<string?>())).Returns((string? token) => token == "test-tab");
        builder.Services.AddSingleton(auth.Object);
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
        await using var app = builder.Build();
        app.UseMiddleware<CookieAuthMiddleware>();
        CodeGraphRoutes.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        var baseAddress = app.Urls.First();
        try
        {
            using var noAuth = await Send(false, false, "{}");
            Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);
            using var sessionOnly = await Send(true, false, "{}");
            Assert.Equal(HttpStatusCode.Unauthorized, sessionOnly.StatusCode);
            git.Verify(service => service.GetRootPathAsync(It.IsAny<CancellationToken>()), Times.Never);
            using var unsafePath = await Send(true, true, "{\"files\":[\"../secret.cs\"]}");
            Assert.Equal(HttpStatusCode.BadRequest, unsafePath.StatusCode);
            git.Verify(service => service.GetRootPathAsync(It.IsAny<CancellationToken>()), Times.Never);
            using var noRepo = await Send(true, true, "{\"files\":[\"src/file.cs\"]}");
            Assert.Equal(HttpStatusCode.BadRequest, noRepo.StatusCode);
            Assert.Contains("Not in a git repository", await noRepo.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
            git.Verify(service => service.GetRootPathAsync(It.IsAny<CancellationToken>()), Times.Once);

            var wire = JsonSerializer.Serialize(new CodeGraphResponse("1.0", new("project"),
                [new("file", "file.cs", "file", "file.cs")], [], DateTime.UtcNow, false, 1, "source"),
                AppJsonSerializerContext.Default.CodeGraphResponse);
            using var document = JsonDocument.Parse(wire);
            Assert.Equal("1.0", document.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal("file.cs", document.RootElement.GetProperty("nodes")[0].GetProperty("path").GetString());
            // Atlas accepts absent optional strings, but rejects explicit nulls.
            var node = document.RootElement.GetProperty("nodes")[0];
            Assert.False(node.TryGetProperty("parentId", out _));
            Assert.False(node.TryGetProperty("language", out _));
            Assert.False(node.TryGetProperty("summary", out _));

            var root = Path.Combine(Path.GetTempPath(), "viberails-graph-route-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                using var init = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git") {
                    WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                    ArgumentList = { "init", "--quiet" }
                });
                await init!.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, init.ExitCode);
                await File.WriteAllTextAsync(Path.Combine(root, "file.cs"), "class Example {}", TestContext.Current.CancellationToken);
                git.Setup(service => service.GetRootPathAsync(It.IsAny<CancellationToken>())).ReturnsAsync(root);
                using var success = await Send(true, true, "{\"files\":[\"file.cs\"]}");
                Assert.Equal(HttpStatusCode.OK, success.StatusCode);
                using var payload = JsonDocument.Parse(await success.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
                Assert.Equal(1, payload.RootElement.GetProperty("fileCount").GetInt32());
                Assert.All(payload.RootElement.GetProperty("nodes").EnumerateArray(), item => {
                    foreach (var field in item.EnumerateObject())
                        Assert.False(field.Value.ValueKind == JsonValueKind.Null, field.Name);
                    Assert.False(string.IsNullOrEmpty(item.GetProperty("path").GetString()));
                });
            }
            finally { Directory.Delete(root, true); }
        }
        finally { await app.StopAsync(CancellationToken.None); }

        async Task<HttpResponseMessage> Send(bool session, bool tab, string body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, baseAddress + "/api/v1/code-analyzer/graph") {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            if (session) request.Headers.Add("viberails_session", "test-session");
            if (tab) request.Headers.Add("viberails_tab", "test-tab");
            return await SharedClient.SendAsync(request, TestContext.Current.CancellationToken);
        }
    }
}
