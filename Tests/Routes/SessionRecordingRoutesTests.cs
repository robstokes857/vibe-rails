using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using VibeRails.Auth;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Middleware;
using VibeRails.Routes;
using Xunit;

namespace Tests.Routes;

public sealed class SessionRecordingRoutesTests
{
    [Fact]
    public async Task StreamingZipPreservesBinaryBase64AndMetadataAndAllRoutesRequireBothCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTime.UtcNow;
        byte[] bytes = [0, 255, 254, 13, 10, 65, 66];
        var recording = new SessionRecordingData(
            [new(1, now, bytes[..1], false), new(2, now.AddSeconds(1), bytes[1..3], true), new(3, now.AddSeconds(2), bytes[3..], false)],
            [new(4, "session", 1, "input", null, now)],
            [new(5, "session", 1, false, [65], 80, 24, now)]);
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        repository.Setup(s => s.GetSessionRecordingAsync("session", It.IsAny<SessionRecordingKind>(), It.IsAny<CancellationToken>())).ReturnsAsync(recording);
        Exception? serverError = null;
        await using var app = BuildApp(repository.Object, ex => serverError = ex);
        await app.StartAsync(ct);
        using var client = new HttpClient { BaseAddress = new(app.Urls.First()) };
        try
        {
            foreach (var route in new[] { "raw-session", "replay", "terminal-replay" })
            {
                foreach (var credentials in new[] { "none", "session", "tab", "both" })
                {
                    using var request = Request(route, credentials);
                    HttpResponseMessage response;
                    try { response = await client.SendAsync(request, ct); }
                    catch (HttpRequestException ex) { throw new Exception("Server failed while streaming.", serverError ?? ex); }
                    using var responseLifetime = response;
                    if (credentials != "both") { Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode); continue; }
                    response.EnsureSuccessStatusCode();
                    if (route == "raw-session")
                    {
                        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
                        using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync(ct)), ZipArchiveMode.Read);
                        Assert.Equal(3, zip.Entries.Count);
                        using var raw = new MemoryStream();
                        await zip.GetEntry("InUTF8.txt")!.Open().CopyToAsync(raw, ct);
                        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray(), raw.ToArray());
                        using var base64 = new StreamReader(zip.GetEntry("bytesBase64.txt")!.Open());
                        Assert.Equal(Convert.ToBase64String(bytes), await base64.ReadToEndAsync(ct));
                        using var metadata = JsonDocument.Parse(zip.GetEntry("sessionLogsAndInputs.json")!.Open());
                        Assert.True(metadata.RootElement.GetProperty("sessionLogs")[1].GetProperty("isError").GetBoolean());
                        Assert.Equal("input", metadata.RootElement.GetProperty("userInputs")[0].GetProperty("inputText").GetString());
                        var expected = new ChatHistoryRawSessionArchiveResponse("session", recording.Chunks.Select(c =>
                            new SessionLogResponse(c.Id, "session", c.TimestampUtc, Convert.ToBase64String(c.Content), c.IsError)).ToList(), recording.Inputs);
                        Assert.Equal(JsonSerializer.Serialize(expected, AppJsonSerializerContext.Default.ChatHistoryRawSessionArchiveResponse), metadata.RootElement.GetRawText());
                    }
                    else
                    {
                        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                        if (route == "replay") Assert.Equal(Convert.ToBase64String(bytes), json.RootElement.GetProperty("content").GetString());
                        else
                        {
                            Assert.Equal(80, json.RootElement.GetProperty("initialCols").GetInt32());
                            Assert.Equal(1000, json.RootElement.GetProperty("frames")[1].GetProperty("delayMs").GetInt64());
                        }
                    }
                }
            }
            repository.Verify(s => s.GetSessionRecordingAsync("session", It.IsAny<SessionRecordingKind>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
            repository.VerifyNoOtherCalls();
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task OversizedRecordingsReturn413AndBusyRequestsReturn429WithoutLoading()
    {
        var ct = TestContext.Current.CancellationToken;
        var repository = new Mock<IRepository>(MockBehavior.Strict);
        var release = new TaskCompletionSource<SessionRecordingData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        repository.Setup(s => s.GetSessionRecordingAsync("session", It.IsAny<SessionRecordingKind>(), It.IsAny<CancellationToken>()))
            .Returns(() => { if (Interlocked.Increment(ref calls) == 2) entered.SetResult(); return release.Task; });
        await using var app = BuildApp(repository.Object);
        await app.StartAsync(ct);
        using var client = new HttpClient { BaseAddress = new(app.Urls.First()) };
        try
        {
            using var firstRequest = Request("raw-session", "both");
            using var secondRequest = Request("terminal-replay", "both");
            var first = client.SendAsync(firstRequest, ct);
            var second = client.SendAsync(secondRequest, ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            using var busyRequest = Request("replay", "both");
            using var busy = await client.SendAsync(busyRequest, ct);
            Assert.Equal(HttpStatusCode.TooManyRequests, busy.StatusCode);
            Assert.Equal(2, calls);
            release.SetException(new SessionRecordingTooLargeException());
            using var firstResponse = await first;
            using var secondResponse = await second;
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, firstResponse.StatusCode);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, secondResponse.StatusCode);
            // Leases were returned on errors; another request reaches storage and gets 413.
            using var retryRequest = Request("replay", "both");
            using var retry = await client.SendAsync(retryRequest, ct);
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, retry.StatusCode);
            Assert.Equal(3, calls);
        }
        finally { release.TrySetResult(null); await app.StopAsync(CancellationToken.None); }
    }

    private static HttpRequestMessage Request(string route, string credentials)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/chatHistory/session/{route}");
        if (credentials is "session" or "both") request.Headers.Add("viberails_session", "session-token");
        if (credentials is "tab" or "both") request.Headers.Add("viberails_tab", "tab-token");
        return request;
    }

    private static WebApplication BuildApp(IRepository repository, Action<Exception>? failed = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(repository);
        builder.Services.AddSingleton(Mock.Of<IChatHistoryService>());
        builder.Services.AddSingleton(Mock.Of<ISessionTranscriptService>());
        var auth = new Mock<IAuthService>();
        auth.Setup(a => a.ValidateToken(It.IsAny<string?>())).Returns((string? token) => token == "session-token");
        auth.Setup(a => a.ValidateTabToken(It.IsAny<string?>())).Returns((string? token) => token == "tab-token");
        builder.Services.AddSingleton(auth.Object);
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonSerializerContext.Default));
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (Exception ex) { failed?.Invoke(ex); throw; }
        });
        app.UseMiddleware<CookieAuthMiddleware>();
        ChatHistoryRoutes.Map(app);
        return app;
    }
}
