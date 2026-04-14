using Moq;
using VibeRails.DTOs;
using VibeRails.Services.BertV2;
using Xunit;

namespace Tests.Services.BertV2;

public class BertCaptureQueryServiceTests
{
    private readonly Mock<IBertSearchDbService> _searchDb;
    private readonly BertCaptureQueryService _service;

    public BertCaptureQueryServiceTests()
    {
        _searchDb = new Mock<IBertSearchDbService>();
        _service = new BertCaptureQueryService(_searchDb.Object, new BertDocumentResponseMapper());
    }

    [Fact]
    public void GetCaptures_MapsPagedDocuments()
    {
        var documents = new[]
        {
            new BertStoredDocument("session-1:10", "user_text:\nrefactor search service\nfile_changes:\n[]", null),
            new BertStoredDocument("session-1:11", "user_text:\nadd tests\nfile_changes:\n[]", null),
        };

        var metadata = new Dictionary<string, BertInputMetadata>
        {
            ["session-1:10"] = new("session-1:10", "session-1", 10, 3, "refactor search service", "abc123", DateTime.UtcNow, "codex", "dev", "C:\\repo", 2),
            ["session-1:11"] = new("session-1:11", "session-1", 11, 4, "add tests", "def456", DateTime.UtcNow, "codex", "dev", "C:\\repo", 0),
        };

        _searchDb.Setup(x => x.GetCaptures(0, 2)).Returns(documents);
        _searchDb.Setup(x => x.CountDocuments()).Returns(12);
        _searchDb.Setup(x => x.GetMetadataByDocumentIds(It.IsAny<IReadOnlyCollection<string>>())).Returns(metadata);

        var response = _service.GetCaptures(0, 2);

        Assert.Equal(12, response.TotalCount);
        Assert.Equal(2, response.Captures.Count);
        Assert.Equal("session-1:10", response.Captures[0].DocumentId);
        Assert.Equal("refactor search service", response.Captures[0].UserTextPreview);
    }

    [Fact]
    public void GetCapture_ThrowsWhenDocumentDoesNotExist()
    {
        _searchDb.Setup(x => x.GetCapture("missing")).Returns((BertStoredDocument?)null);

        Assert.Throws<KeyNotFoundException>(() => _service.GetCapture("missing"));
    }

    [Fact]
    public void GetCapture_UsesFallbackParsingWhenMetadataIsMissing()
    {
        var document = new BertStoredDocument(
            "session-7:42",
            "git_commit: abc123\nuser_text:\nsearch the vector db\nfile_changes:\n[]",
            null);

        _searchDb.Setup(x => x.GetCapture("session-7:42")).Returns(document);
        _searchDb.Setup(x => x.GetMetadataByDocumentIds(It.IsAny<IReadOnlyCollection<string>>()))
            .Returns(new Dictionary<string, BertInputMetadata>());
        _searchDb.Setup(x => x.GetFileChanges(42)).Returns(Array.Empty<BertFileChangeResponse>());

        var response = _service.GetCapture("session-7:42");

        Assert.Equal("session-7", response.SessionId);
        Assert.Equal(42, response.UserInputId);
        Assert.Equal("abc123", response.GitCommitHash);
        Assert.Equal("search the vector db", response.UserText);
    }
}
