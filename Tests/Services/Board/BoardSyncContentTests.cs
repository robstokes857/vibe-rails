using System.Security.Cryptography;
using System.Text.Json;
using Moq;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Board.Sync;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardSyncContentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task LargeAttachmentAndAllCommitFilesRoundTripWithoutDisplayTruncation()
    {
        var attachment = new byte[2 * 1024 * 1024]; new Random(7).NextBytes(attachment);
        var longText = new string('x', 300_000);
        var files = Enumerable.Range(0, 101).Select(i => new SandboxDiffFileResponse("file-" + i, "text", i == 0 ? longText : "before", "after")).ToList();
        var snapshot = new SandboxDiffResponse(files, 101);
        var now = DateTime.UtcNow;
        var sha = new string('a', 40);
        var metadata = new BoardSyncActivityRecord([], [new("card", sha, "Author", "Change", now, now)],
            [new("attachment", "card", "large.bin", "application/octet-stream", attachment.Length, now)], []);
        var store = new Mock<IBoardStore>();
        store.Setup(s => s.GetAutomationSessionIdsAsync("project", It.IsAny<IReadOnlyList<string>>(), Ct)).ReturnsAsync(new HashSet<string>());
        store.Setup(s => s.GetSyncSessionOutcomesAsync(It.IsAny<IReadOnlyList<string>>(), Ct)).ReturnsAsync(new Dictionary<string, BoardSessionOutcomeRecord>());
        store.Setup(s => s.GetSyncCommitSnapshotAsync("project", "card", sha, BoardSyncContent.MaxContentBytes, Ct)).ReturnsAsync(snapshot);
        store.Setup(s => s.GetSyncAttachmentContentAsync("project", "card", "attachment", BoardSyncContent.MaxContentBytes, Ct)).ReturnsAsync(attachment);
        var sent = new Dictionary<string, byte[]>();
        var client = new Mock<IBoardSyncClient>(MockBehavior.Strict);
        client.Setup(c => c.PutContentPartAsync(It.IsAny<BoardSyncContentPartWire>(), It.IsAny<ReadOnlyMemory<byte>>(), Ct, "account"))
            .Callback<BoardSyncContentPartWire, ReadOnlyMemory<byte>, CancellationToken, string?>((p, bytes, _, _) => sent[p.Sha256] = bytes.ToArray())
            .Returns(Task.CompletedTask);
        using var cache = new BoardSyncActivityCache();
        var link = new BoardSyncLinkRecord("board", "remote", 0, true, null, null, null, default, default, ProjectPath: "project", DestinationKey: "account");
        var result = await BoardSyncContent.CaptureAsync(store.Object, client.Object, cache, "identity", link, "card", metadata, Ct);
        Assert.Equal(2, result.Schema);
        byte[] Read(BoardSyncContentWire content) => content.Parts.SelectMany(p => sent[p.Sha256]).ToArray();
        Assert.Equal(attachment, Read(Assert.Single(result.Attachments).Content!));
        var recovered = JsonSerializer.Deserialize(Read(Assert.Single(result.Commits).Content!), BoardSyncJsonContext.Default.SandboxDiffResponse)!;
        Assert.Equal(101, recovered.Files.Count);
        Assert.Equal(longText, recovered.Files[0].OriginalContent);
        Assert.Equal(snapshot.Files[^1], recovered.Files[^1]);
        Assert.Empty(result.Commits[0].Files);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(result, BoardSyncJsonContext.Default.BoardSyncActivityWire).Length < 8192);
    }

    [Fact]
    public async Task FailedPartIsRetried_AndSuccessfulPartsAreReused()
    {
        var bytes = new byte[5 * 1024 * 1024]; new Random(9).NextBytes(bytes);
        var client = new Mock<IBoardSyncClient>();
        var attempts = 0;
        client.Setup(c => c.PutContentPartAsync(It.IsAny<BoardSyncContentPartWire>(), It.IsAny<ReadOnlyMemory<byte>>(), Ct, "account"))
            .Returns(() => ++attempts == 1 ? Task.FromException(new IOException("offline")) : Task.CompletedTask);
        using var cache = new BoardSyncActivityCache();
        await Assert.ThrowsAsync<IOException>(() => BoardSyncContent.UploadAsync(client.Object, cache, "identity", "account", bytes, Ct));
        var result = await BoardSyncContent.UploadAsync(client.Object, cache, "identity", "account", bytes, Ct);
        Assert.Equal(2, result.Parts.Count);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), result.Sha256);
        await BoardSyncContent.UploadAsync(client.Object, cache, "identity", "account", bytes, Ct);
        Assert.Equal(3, attempts);
    }
}
