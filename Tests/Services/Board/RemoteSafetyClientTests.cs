using System.Net;
using VibeRails.Services.Board.Sync;
using Xunit;

namespace Tests.Services.Board;

public sealed class RemoteSafetyClientTests
{
    [Fact]
    public async Task HoldSurvivesRestartKeyRotationAndExpiryUntilExplicitReapproval()
    {
        var folder = Path.Combine(Path.GetTempPath(), "safety-test-" + Guid.NewGuid().ToString("N"));
        var phase = "locked"; var generation = "0"; var account = 1; var blocked = false;
        using var handler = new Handler(() => $$"""{"accountId":{{account}},"generation":"{{generation}}","remoteWork":{"state":"{{phase}}","unlockUtc":null},"uploadsBlocked":{{blocked.ToString().ToLowerInvariant()}}}""", terminalOnly: true);
        RemoteSafetyClient Client(string key) => new(() => new HttpClient(handler, false), () => new Uri("https://server.invalid"), () => key, stateDirectory: folder);
        try
        {
            Assert.Equal("board_remote_sync_paused", (await Assert.ThrowsAsync<BoardSyncClientException>(() => Client("first-key").EnsureAsync(true, false, TestContext.Current.CancellationToken))).Code);
            phase = "recovery_required";
            Assert.True((await Client("rotated-key").StatusAsync(TestContext.Current.CancellationToken)).LocalReviewRequired);
            phase = "enabled"; generation = "1";
            Assert.Equal("account_remote_reapproval_required", (await Assert.ThrowsAsync<BoardSyncClientException>(() => Client("rotated-key").EnsureAsync(true, false, TestContext.Current.CancellationToken))).Code);
            blocked = true;
            var approved = await Client("rotated-key").ApproveAsync("1", 1, "https://server.invalid", TestContext.Current.CancellationToken);
            Assert.False(approved.LocalReviewRequired);
            Assert.True(approved.UploadReviewRequired);
            Assert.Equal("1", await Client("rotated-key").EnsureAsync(true, false, TestContext.Current.CancellationToken));
            Assert.Equal("account_uploads_blocked", (await Assert.ThrowsAsync<BoardSyncClientException>(() => Client("rotated-key").EnsureAsync(false, true, TestContext.Current.CancellationToken))).Code);
            blocked = false;
            Assert.Equal("account_remote_reapproval_required", (await Assert.ThrowsAsync<BoardSyncClientException>(() => Client("rotated-key").EnsureAsync(false, true, TestContext.Current.CancellationToken))).Code);
            await Client("rotated-key").ApproveAsync("1", 1, "https://server.invalid", TestContext.Current.CancellationToken);
            Assert.Equal("1", await Client("rotated-key").EnsureAsync(true, true, TestContext.Current.CancellationToken));
            account = 2; generation = "0";
            Assert.Equal("0", await Client("other-account").EnsureAsync(true, true, TestContext.Current.CancellationToken));
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    private sealed class Handler(Func<string> response, bool terminalOnly = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(terminalOnly && request.RequestUri!.AbsolutePath.Contains("/boards/") ? HttpStatusCode.Forbidden : HttpStatusCode.OK) { Content = new StringContent(response()) });
    }
}
