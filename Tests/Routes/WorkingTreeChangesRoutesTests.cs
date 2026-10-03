using System.Diagnostics;
using System.Net;
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

public sealed class WorkingTreeChangesRoutesTests
{
    // One client per class: a client per test leaves sockets in TIME_WAIT across the suite.
    private static readonly HttpClient SharedClient = new();

    [Fact]
    public async Task Changes_RequireBothCredentials_RefuseUnsafePaths_AndServeBoundedDiffs()
    {
        var token = TestContext.Current.CancellationToken;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var git = new Mock<IGitService>();
        git.Setup(service => service.GetRootPathAsync(It.IsAny<CancellationToken>())).ReturnsAsync("");
        builder.Services.AddSingleton(git.Object);
        builder.Services.AddSingleton<RepositoryCodeGraph>();
        builder.Services.AddSingleton<WorkingTreeChanges>();
        var auth = new Mock<IAuthService>();
        auth.Setup(service => service.ValidateToken(It.IsAny<string?>())).Returns((string? value) => value == "test-session");
        auth.Setup(service => service.ValidateTabToken(It.IsAny<string?>())).Returns((string? value) => value == "test-tab");
        builder.Services.AddSingleton(auth.Object);
        builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
        await using var app = builder.Build();
        app.UseMiddleware<CookieAuthMiddleware>();
        CodeGraphRoutes.Map(app);
        await app.StartAsync(token);
        var baseAddress = app.Urls.First();
        try
        {
            foreach (var path in new[] { "/api/v1/code-analyzer/changes", "/api/v1/code-analyzer/changes/diff?path=file.cs" })
            {
                using var noAuth = await Send(path, false, false);
                Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);
                using var sessionOnly = await Send(path, true, false);
                Assert.Equal(HttpStatusCode.Unauthorized, sessionOnly.StatusCode);
            }
            git.Verify(service => service.GetRootPathAsync(It.IsAny<CancellationToken>()), Times.Never);

            // Unsafe paths are refused before the repository root is even resolved.
            foreach (var unsafePath in new[] { "../secret.cs", "/etc/passwd", "C:/windows/win.ini", "a/../../b", "" })
            {
                using var refused = await Send("/api/v1/code-analyzer/changes/diff?path=" + Uri.EscapeDataString(unsafePath), true, true);
                Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            }
            using var missingPath = await Send("/api/v1/code-analyzer/changes/diff", true, true);
            Assert.Equal(HttpStatusCode.BadRequest, missingPath.StatusCode);
            using var unsafeOriginal = await Send("/api/v1/code-analyzer/changes/diff?path=file.cs&original=" + Uri.EscapeDataString("../secret.cs"), true, true);
            Assert.Equal(HttpStatusCode.BadRequest, unsafeOriginal.StatusCode);
            git.Verify(service => service.GetRootPathAsync(It.IsAny<CancellationToken>()), Times.Never);

            using var noRepo = await Send("/api/v1/code-analyzer/changes", true, true);
            Assert.Equal(HttpStatusCode.BadRequest, noRepo.StatusCode);
            Assert.Contains("Not in a git repository", await noRepo.Content.ReadAsStringAsync(token));
            using var noRepoDiff = await Send("/api/v1/code-analyzer/changes/diff?path=file.cs", true, true);
            Assert.Equal(HttpStatusCode.BadRequest, noRepoDiff.StatusCode);

            // Source-generated serialization: camelCase names, absent counts and paths omitted rather than null.
            var wire = JsonSerializer.Serialize(new WorkingTreeChangesResponse(1, 2, 0, false,
                [new WorkingTreeChangeFile("image.png", "modified", true, false, null, null, true)], DateTime.UtcNow),
                AppJsonSerializerContext.Default.WorkingTreeChangesResponse);
            using (var document = JsonDocument.Parse(wire))
            {
                var file = document.RootElement.GetProperty("files")[0];
                Assert.Equal("image.png", file.GetProperty("path").GetString());
                Assert.True(file.GetProperty("binary").GetBoolean());
                Assert.False(file.TryGetProperty("additions", out _));
                Assert.False(file.TryGetProperty("originalPath", out _));
                Assert.False(document.RootElement.TryGetProperty("head", out _));
                Assert.Equal(1, document.RootElement.GetProperty("count").GetInt32());
            }

            var root = Path.Combine(Path.GetTempPath(), "viberails-changes-route-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                await Git(root, token, "init", "--quiet");
                await Git(root, token, "config", "user.email", "tests@example.com");
                await Git(root, token, "config", "user.name", "Tests");
                await File.WriteAllTextAsync(Path.Combine(root, "file.cs"), "class Example {}\n", token);
                await File.WriteAllTextAsync(Path.Combine(root, "keep.cs"), "class Keep {}\n", token);
                await Git(root, token, "add", "--all");
                await Git(root, token, "commit", "--quiet", "-m", "base");
                await File.WriteAllTextAsync(Path.Combine(root, "file.cs"), "class Example { int Added; }\n", token);
                await File.WriteAllTextAsync(Path.Combine(root, "notes.md"), "# notes\n", token);
                await Git(root, token, "mv", "keep.cs", "moved.cs");
                git.Setup(service => service.GetRootPathAsync(It.IsAny<CancellationToken>())).ReturnsAsync(root);

                using var list = await Send("/api/v1/code-analyzer/changes", true, true);
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                using var payload = JsonDocument.Parse(await list.Content.ReadAsStringAsync(token));
                Assert.Equal(3, payload.RootElement.GetProperty("count").GetInt32());
                Assert.False(payload.RootElement.GetProperty("truncated").GetBoolean());
                var files = payload.RootElement.GetProperty("files").EnumerateArray().ToDictionary(
                    item => item.GetProperty("path").GetString()!, item => item);
                Assert.Equal("modified", files["file.cs"].GetProperty("status").GetString());
                Assert.Equal(1, files["file.cs"].GetProperty("additions").GetInt32());
                Assert.Equal(1, files["file.cs"].GetProperty("deletions").GetInt32());
                Assert.Equal("untracked", files["notes.md"].GetProperty("status").GetString());
                Assert.Equal("renamed", files["moved.cs"].GetProperty("status").GetString());
                Assert.Equal("keep.cs", files["moved.cs"].GetProperty("originalPath").GetString());
                Assert.All(payload.RootElement.GetProperty("files").EnumerateArray(), item => {
                    foreach (var field in item.EnumerateObject())
                        Assert.False(field.Value.ValueKind == JsonValueKind.Null, field.Name);
                });

                using var diff = await Send("/api/v1/code-analyzer/changes/diff?path=file.cs", true, true);
                Assert.Equal(HttpStatusCode.OK, diff.StatusCode);
                using var diffPayload = JsonDocument.Parse(await diff.Content.ReadAsStringAsync(token));
                Assert.Equal("file.cs", diffPayload.RootElement.GetProperty("fileName").GetString());
                Assert.Equal("csharp", diffPayload.RootElement.GetProperty("language").GetString());
                Assert.Equal("modified", diffPayload.RootElement.GetProperty("status").GetString());
                Assert.Equal("class Example {}\n", diffPayload.RootElement.GetProperty("originalContent").GetString());
                Assert.Equal("class Example { int Added; }\n", diffPayload.RootElement.GetProperty("modifiedContent").GetString());
                Assert.False(diffPayload.RootElement.GetProperty("binary").GetBoolean());
                Assert.False(diffPayload.RootElement.GetProperty("truncated").GetBoolean());

                using var added = await Send("/api/v1/code-analyzer/changes/diff?path=notes.md", true, true);
                Assert.Equal(HttpStatusCode.OK, added.StatusCode);
                Assert.Contains("\"status\":\"added\"", await added.Content.ReadAsStringAsync(token));
                using var unknown = await Send("/api/v1/code-analyzer/changes/diff?path=never/existed.cs", true, true);
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
                using var renamed = await Send("/api/v1/code-analyzer/changes/diff?path=moved.cs&original=keep.cs", true, true);
                Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
                using var renamedPayload = JsonDocument.Parse(await renamed.Content.ReadAsStringAsync(token));
                Assert.Equal("renamed", renamedPayload.RootElement.GetProperty("status").GetString());
                Assert.Equal("class Keep {}\n", renamedPayload.RootElement.GetProperty("originalContent").GetString());
                Assert.Equal("class Keep {}\n", renamedPayload.RootElement.GetProperty("modifiedContent").GetString());
            }
            finally
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, true);
            }
        }
        finally { await app.StopAsync(CancellationToken.None); }

        async Task<HttpResponseMessage> Send(string path, bool session, bool tab)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, baseAddress + path);
            if (session) request.Headers.Add("viberails_session", "test-session");
            if (tab) request.Headers.Add("viberails_tab", "test-tab");
            return await SharedClient.SendAsync(request, token);
        }
    }

    private static async Task Git(string root, CancellationToken token, params string[] arguments)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!;
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {await error}");
    }
}
