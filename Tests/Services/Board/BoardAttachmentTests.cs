using System.Text;
using Microsoft.Data.Sqlite;
using Moq;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Mcp.Tools;
using Xunit;

namespace Tests.Services.Board;

public sealed class BoardAttachmentTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vb-board-files-" + Guid.NewGuid().ToString("N"));
    private readonly string _connectionString;
    private readonly string _project;
    private readonly BoardStore _store;
    private readonly BoardService _service;
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BoardAttachmentTests()
    {
        Directory.CreateDirectory(_directory);
        _project = Path.Combine(_directory, "project");
        _connectionString = $"Data Source={Path.Combine(_directory, "board.db")};Pooling=False";
        _store = new BoardStore(_connectionString, _connectionString);
        var live = new Mock<IBoardLiveSessionProbe>();
        live.Setup(x => x.GetLiveSessionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Dictionary<string, string>());
        _service = new BoardService(_store, Mock.Of<IBoardCommitService>(), live.Object);
    }

    [Fact]
    public async Task FilesRoundTripExactBytes_ServerCountsBytes_AndSeparatesMetadata()
    {
        var card = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Files"), Ct);
        var bytes = Encoding.UTF8.GetBytes("# Scope\n\n<svg onload=alert(1)>\nRésumé ✓");
        var uploaded = await _service.AddAttachmentAsync(_project, card.Id,
            Request("../scope.MD", bytes, declaredBytes: 1, declaredMime: "image/png"), Ct);
        Assert.NotNull(uploaded);
        Assert.Equal("scope.MD", uploaded.Name);
        Assert.Equal(bytes.Length, uploaded.Bytes);
        Assert.Equal("text/markdown", uploaded.MimeType);
        Assert.Empty(uploaded.Url);
        var content = await _service.GetAttachmentContentAsync(_project, card.Key, uploaded.Id, Ct);
        Assert.Equal(bytes, content!.Content);
        Assert.Equal("# Scope", BoardService.ReadAttachmentText(content, 0, 7));
        Assert.Null(await _service.GetAttachmentContentAsync(Path.Combine(_directory, "other"), card.Id, uploaded.Id, Ct));
        var otherCard = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Other"), Ct);
        Assert.Null(await _service.GetAttachmentContentAsync(_project, otherCard.Id, uploaded.Id, Ct));
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT length(Content) FROM BoardAttachmentContents WHERE AttachmentId = $id";
        command.Parameters.AddWithValue("$id", uploaded.Id);
        Assert.Equal(bytes.Length, Convert.ToInt32(await command.ExecuteScalarAsync(Ct)));
    }

    [Fact]
    public async Task DeletingFileRemovesItsBytes_AndCannotBeReadAgain()
    {
        var card = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Files"), Ct);
        var attachment = (await _service.AddAttachmentAsync(_project, card.Id, Request("scope.txt", "first scope"u8.ToArray()), Ct))!;
        Assert.True(await _service.DeleteAttachmentAsync(_project, card.Id, attachment.Id, Ct));
        Assert.False(await _service.DeleteAttachmentAsync(_project, card.Id, attachment.Id, Ct));
        Assert.Empty((await _service.GetCardAsync(_project, card.Id, Ct))!.Attachments);
        Assert.Null(await _service.GetAttachmentContentAsync(_project, card.Id, attachment.Id, Ct));
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(Ct);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM BoardAttachmentContents;";
        Assert.Equal(0L, await command.ExecuteScalarAsync(Ct));
    }

    [Fact]
    public async Task RejectsMalformedUploads_AndIgnoresMimeSpoofing()
    {
        var card = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Files"), Ct);
        foreach (var malformed in new[] { "data:text/plain,hello", "data:text/plain;base64,%%%", "data:text/plain;base64,A", "data:text/plain;base64,Y Q==" })
            await Assert.ThrowsAsync<BoardValidationException>(() => _service.AddAttachmentAsync(_project, card.Id, new AddBoardAttachmentRequest("x.txt", malformed), Ct));
        // Size is deliberately not validated: there is no per-file or per-card byte limit.
        var svg = (await _service.AddAttachmentAsync(_project, card.Id, Request("evil.svg", "<svg onload=alert(1)>"u8.ToArray(), declaredMime: "image/png"), Ct))!;
        Assert.Equal("application/octet-stream", svg.MimeType);
        Assert.Empty(svg.Url);
        var empty = (await _service.AddAttachmentAsync(_project, card.Id, Request("empty.txt", []), Ct))!;
        Assert.Equal(0, empty.Bytes);
        Assert.Empty((await _service.GetAttachmentContentAsync(_project, card.Id, empty.Id, Ct))!.Content);
    }

    [Fact]
    public async Task ConcurrentUploadsCannotBypassAttachmentCountLimit()
    {
        var card = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Files"), Ct);
        for (var i = 0; i < BoardService.MaxAttachmentsPerCard - 1; i++)
            await _service.AddAttachmentAsync(_project, card.Id, Request($"{i}.txt", []), Ct);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 3).Select(async i =>
        {
            try { await _service.AddAttachmentAsync(_project, card.Id, Request($"last{i}.txt", []), Ct); return true; }
            catch (BoardValidationException) { return false; }
        }));
        Assert.Single(outcomes, success => success);
        Assert.Equal(BoardService.MaxAttachmentsPerCard, (await _service.GetCardAsync(_project, card.Id, Ct))!.Attachments.Count);
    }

    [Fact]
    public async Task TextReaderRejectsBinaryAndInvalidUtf8_AndBoundsChunks()
    {
        var card = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Files"), Ct);
        var binary = (await _service.AddAttachmentAsync(_project, card.Id, Request("data.bin", [0, 1, 2]), Ct))!;
        Assert.Throws<BoardValidationException>(() => BoardService.ReadAttachmentText(
            new BoardAttachmentContent(new BoardAttachmentRecord(binary.Id, card.Id, binary.Name, binary.MimeType, 3, "", DateTime.UtcNow), [0, 1, 2])));
        var badText = (await _service.AddAttachmentAsync(_project, card.Id, Request("bad.txt", [255, 255]), Ct))!;
        var content = (await _service.GetAttachmentContentAsync(_project, card.Id, badText.Id, Ct))!;
        Assert.Throws<BoardValidationException>(() => BoardService.ReadAttachmentText(content));
        Assert.Throws<BoardValidationException>(() => BoardService.ReadAttachmentText(content, -1));
        Assert.Throws<BoardValidationException>(() => BoardService.ReadAttachmentText(content, 0, 100_001));
    }

    [Fact]
    public async Task McpListsAttachmentIds_AndReadsBoundedCardScopedText()
    {
        var card = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Files"), Ct);
        var file = (await _service.AddAttachmentAsync(_project, card.Id, Request("scope.md", "abcdef"u8.ToArray()), Ct))!;
        var resolver = new Mock<IBoardProjectResolver>();
        resolver.Setup(x => x.ResolveAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_project);
        var tool = new BoardTool(_service, resolver.Object, _store);
        var detail = await tool.GetBoardCard(card.Key, cancellationToken: Ct);
        Assert.Contains($"{file.Id}: scope.md (text/markdown, 6 bytes)", detail);
        Assert.Contains("read_board_attachment", detail);
        var chunk = await tool.ReadBoardAttachment(file.Id, card.Key, 2, 3, Ct);
        Assert.Contains("Offset 2; returned 3 characters", chunk);
        Assert.EndsWith("cde", chunk);
        Assert.StartsWith("FAIL:", await tool.ReadBoardAttachment(file.Id, card.Key, -1, 3, Ct));
        var another = await _service.CreateCardAsync(_project, new CreateBoardCardRequest(Title: "Other"), Ct);
        Assert.StartsWith("FAIL: attachment not found", await tool.ReadBoardAttachment(file.Id, another.Key, cancellationToken: Ct));
    }

    private static AddBoardAttachmentRequest Request(string name, byte[] content, long? declaredBytes = null, string? declaredMime = null) =>
        new(name, "data:application/octet-stream;base64," + Convert.ToBase64String(content), declaredBytes ?? content.Length, declaredMime ?? "application/octet-stream");

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
