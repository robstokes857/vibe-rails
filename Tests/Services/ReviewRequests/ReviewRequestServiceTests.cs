using System.Text;
using Microsoft.Data.Sqlite;
using Moq;
using Tests.VcaRegression;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Git;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.ReviewRequests;
using VibeRails.Services.VCA;
using Xunit;

namespace Tests.Services.ReviewRequests;

/// <summary>
/// The review-request core (VB-50, slice A) against a real Git repository, the real staged-snapshot
/// reader and a real state.db under a temporary directory; only viberails.ai is a fake.
/// </summary>
public sealed class ReviewRequestServiceTests : IAsyncLifetime
{
    private const string BaseText = "class Db { }\n";
    private const string StagedText = "class Db { int Version = 2; }\n";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vb-review-request-" + Guid.NewGuid().ToString("N"));
    private string Repo => Path.Combine(_directory, "repo");
    private string Connection => new SqliteConnectionStringBuilder { DataSource = Path.Combine(_directory, "state.db"), Pooling = false }.ToString();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ReviewRequestStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Repo);
        await Git("init", "-q");
        await Git("config", "user.email", "review-test@example.invalid");
        await Git("config", "user.name", "Review test");
        await Git("config", "core.autocrlf", "false");
        await Git("config", "commit.gpgsign", "false");
        await File.WriteAllTextAsync(Path.Combine(Repo, "db.cs"), BaseText, Ct);
        await File.WriteAllTextAsync(Path.Combine(Repo, "other.txt"), "other\n", Ct);
        await Git("add", "-A", "--", ".");
        await Git("-c", "core.hooksPath=", "commit", "-q", "-m", "baseline");
        _store = new ReviewRequestStore(Connection);
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_directory, recursive: true);
        }
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task CreateCapturesTheStagedTreeWithBoundedFileContent()
    {
        await StageChangesAsync();
        var image = new byte[64];
        image[0] = 0x89; image[1] = 0x50; image[2] = 0; image[3] = 0x0A;
        await File.WriteAllBytesAsync(Path.Combine(Repo, "logo.png"), image, Ct);
        await File.WriteAllTextAsync(Path.Combine(Repo, "big.txt"), new string('x', ReviewBundleCapture.MaxFileContentBytes + 10), Ct);
        await Git("add", "--", "logo.png", "big.txt");
        var tree = (await Git("write-tree")).Trim();
        var head = (await Git("rev-parse", "HEAD")).Trim();
        var service = Service(FakeCommitSignatureVerifier.ValidFor("owner@example.com"));

        var created = await service.CreateAsync(Repo, new CreateReviewRequest(
            Reviewers: ["Owner@Example.com", "owner@example.com", " second@example.com "],
            CardKey: "VB-50",
            Note: "  Please check the migration.  "), sessions: null, Ct);

        Assert.Equal(ReviewRequestStatus.Open, created.Status);
        Assert.Equal(tree, created.TreeId);
        Assert.Equal(head, created.BaseCommit);
        Assert.Equal(["Owner@Example.com", "second@example.com"], created.Reviewers);
        Assert.Equal("VB-50", created.CardKey);
        Assert.Equal(3, created.FileCount);
        Assert.Equal(0, created.SessionCount);
        Assert.Empty(created.Decisions);

        var detail = await service.GetAsync(Repo, created.Id, Ct);
        var bundle = detail.Bundle;
        Assert.Equal(ReviewBundleCapture.SchemaVersion, bundle.Schema);
        Assert.Equal("repo", bundle.Repository);
        Assert.Equal(ReviewScope.Staged, bundle.Scope);
        Assert.Equal("Please check the migration.", bundle.Note);
        Assert.Equal(["big.txt", "db.cs", "logo.png"], bundle.Files.Select(file => file.Path));

        var db = bundle.Files[1];
        Assert.Equal("modified", db.Change);
        Assert.Equal(BaseText, db.Before);
        Assert.Equal(StagedText, db.After);
        Assert.Null(db.Omitted);

        var logo = bundle.Files[2];
        Assert.True(logo.Binary);
        Assert.Equal(ReviewBundleOmission.Binary, logo.Omitted);
        Assert.Null(logo.After);

        var big = bundle.Files[0];
        Assert.Equal("added", big.Change);
        Assert.Equal(ReviewBundleOmission.Oversized, big.Omitted);
        Assert.Null(big.After);

        // The bundle a reviewer reads is the stored row, not a re-read of the index.
        var stored = await _store.GetAsync(created.Id, Ct);
        Assert.NotNull(stored);
        Assert.Contains("\"treeId\":\"" + tree + "\"", stored.BundleJson);
        Assert.DoesNotContain("\"omitted\":null", stored.BundleJson);
    }

    [Fact]
    public async Task CreateRefusesAnEmptyIndexAndBadInput()
    {
        var service = Service(FakeCommitSignatureVerifier.ValidFor("owner@example.com"));

        var nothing = await Assert.ThrowsAsync<ReviewRequestException>(() =>
            service.CreateAsync(Repo, new CreateReviewRequest(Reviewers: ["owner@example.com"]), null, Ct));
        Assert.Equal("nothing_staged", nothing.Code);
        Assert.Equal(409, nothing.Status);

        await StageChangesAsync();
        await AssertInvalidAsync(service, new CreateReviewRequest());
        await AssertInvalidAsync(service, new CreateReviewRequest(Reviewers: ["not-an-email"]));
        await AssertInvalidAsync(service, new CreateReviewRequest(Reviewers: Enumerable.Range(0, 11).Select(i => $"r{i}@example.com").ToList()));
        await AssertInvalidAsync(service, new CreateReviewRequest(Scope: "head", Reviewers: ["owner@example.com"]));
        await AssertInvalidAsync(service, new CreateReviewRequest(Reviewers: ["owner@example.com"], Sessions: ["not-a-session"]));
        await AssertInvalidAsync(service, new CreateReviewRequest(Reviewers: ["owner@example.com"], Note: new string('n', ReviewRequestService.MaxNoteChars + 1)));

        var notARepo = Path.Combine(_directory, "plain");
        Directory.CreateDirectory(notARepo);
        var noRepo = await Assert.ThrowsAsync<ReviewRequestException>(() =>
            service.CreateAsync(notARepo, new CreateReviewRequest(Reviewers: ["owner@example.com"]), null, Ct));
        Assert.Equal("not_a_repository", noRepo.Code);
        Assert.Empty((await service.ListAsync(Repo, Ct)).Requests);
    }

    [Fact]
    public async Task AttachedSessionsAreLookedUpAndNamed()
    {
        await StageChangesAsync();
        var known = Guid.NewGuid().ToString("D");
        var sessions = new Mock<ISessionStore>(MockBehavior.Strict);
        sessions.Setup(s => s.GetSessionByIdAsync(known, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SessionResponse(known, "claude", "Review env", Repo, new DateTime(2026, 10, 23, 8, 0, 0, DateTimeKind.Utc), null, null));
        sessions.Setup(s => s.GetSessionDisplayInfoAsync(known)).ReturnsAsync(("claude", "Fix the migration"));
        var missing = Guid.NewGuid().ToString("D");
        sessions.Setup(s => s.GetSessionByIdAsync(missing, It.IsAny<CancellationToken>())).ReturnsAsync((SessionResponse?)null);
        var service = Service(FakeCommitSignatureVerifier.ValidFor("owner@example.com"));

        var notFound = await Assert.ThrowsAsync<ReviewRequestException>(() => service.CreateAsync(Repo,
            new CreateReviewRequest(Reviewers: ["owner@example.com"], Sessions: [known, missing]), sessions.Object, Ct));
        Assert.Equal("session_not_found", notFound.Code);
        Assert.Equal(404, notFound.Status);

        var created = await service.CreateAsync(Repo,
            new CreateReviewRequest(Reviewers: ["owner@example.com"], Sessions: [known, known.ToUpperInvariant()]), sessions.Object, Ct);
        Assert.Equal(1, created.SessionCount);
        var session = Assert.Single((await service.GetAsync(Repo, created.Id, Ct)).Bundle.Sessions);
        Assert.Equal(known, session.Id);
        Assert.Equal("Fix the migration", session.Name);
        Assert.Equal("claude", session.Cli);
        Assert.Null(session.EndedUtc);
    }

    [Fact]
    public async Task ApprovalMustCoverTheTreeVerifyAndComeFromAReviewer()
    {
        await StageChangesAsync();
        var tree = (await Git("write-tree")).Trim();
        var verifier = new SwitchableVerifier();
        var service = Service(verifier);
        var created = await service.CreateAsync(Repo, new CreateReviewRequest(Reviewers: ["owner@example.com"]), null, Ct);

        var notATrailer = await Assert.ThrowsAsync<ReviewRequestException>(() =>
            service.RecordDecisionAsync(Repo, created.Id, new RecordReviewDecisionRequest("approved", Trailer: "looks good"), Ct));
        Assert.Equal("invalid_request", notATrailer.Code);

        var otherTree = await Assert.ThrowsAsync<ReviewRequestException>(() =>
            service.RecordDecisionAsync(Repo, created.Id, new RecordReviewDecisionRequest("approved", Trailer: Trailer(new string('0', 40))), Ct));
        Assert.Equal("tree_mismatch", otherTree.Code);
        Assert.Empty(verifier.Calls);

        verifier.Answer = new CommitSignatureVerification(CommitSignatureStatus.Unavailable, "viberails.ai could not be reached");
        var unavailable = await Assert.ThrowsAsync<ReviewRequestException>(() =>
            service.RecordDecisionAsync(Repo, created.Id, new RecordReviewDecisionRequest("approved", Trailer: Trailer(tree)), Ct));
        Assert.Equal("verification_unavailable", unavailable.Code);
        Assert.Equal(503, unavailable.Status);

        verifier.Answer = new CommitSignatureVerification(CommitSignatureStatus.Rejected, "the signing key has been revoked");
        var rejected = await Assert.ThrowsAsync<ReviewRequestException>(() =>
            service.RecordDecisionAsync(Repo, created.Id, new RecordReviewDecisionRequest("approved", Trailer: Trailer(tree)), Ct));
        Assert.Equal("signature_rejected", rejected.Code);
        Assert.Contains("revoked", rejected.Message);

        verifier.Answer = new CommitSignatureVerification(CommitSignatureStatus.Valid, "valid", "intruder@example.com");
        var stranger = await Assert.ThrowsAsync<ReviewRequestException>(() =>
            service.RecordDecisionAsync(Repo, created.Id, new RecordReviewDecisionRequest("approved", Trailer: Trailer(tree)), Ct));
        Assert.Equal("not_a_reviewer", stranger.Code);

        // Nothing above was stored, and the hook lookup sees nothing.
        Assert.Empty((await service.GetAsync(Repo, created.Id, Ct)).Request.Decisions);
        Assert.Empty(await _store.FindVerifiedApprovalsAsync(tree, Ct));

        // viberails.ai returns the account's email as stored; reviewers compare without case.
        verifier.Answer = new CommitSignatureVerification(CommitSignatureStatus.Valid, "valid", "OWNER@example.com");
        var approved = await service.RecordDecisionAsync(Repo, created.Id,
            new RecordReviewDecisionRequest("approved", Trailer: Trailer(tree), Comment: "Ship it"), Ct);
        Assert.Equal(ReviewRequestStatus.Approved, approved.Status);
        var decision = Assert.Single(approved.Decisions);
        Assert.Equal("OWNER@example.com", decision.Reviewer);
        Assert.Equal("Ship it", decision.Comment);
        Assert.Equal(Trailer(tree), decision.Trailer);
        Assert.NotNull(decision.VerifiedUtc);

        var approvals = await _store.FindVerifiedApprovalsAsync(tree, Ct);
        var row = Assert.Single(approvals);
        Assert.Same(row, ReviewRequestService.SelectApproval(approvals, ["owner@example.com"]));
        Assert.Same(row, ReviewRequestService.SelectApproval(approvals, []));
        Assert.Null(ReviewRequestService.SelectApproval(approvals, ["nobody@example.com"]));
    }

    [Fact]
    public async Task RejectionNeedsAListedReviewerAndAnApprovalStillWins()
    {
        await StageChangesAsync();
        var tree = (await Git("write-tree")).Trim();
        var service = Service(FakeCommitSignatureVerifier.ValidFor("owner@example.com"));
        var created = await service.CreateAsync(Repo, new CreateReviewRequest(Reviewers: ["owner@example.com", "second@example.com"]), null, Ct);

        var anonymous = await Assert.ThrowsAsync<ReviewRequestException>(() =>
            service.RecordDecisionAsync(Repo, created.Id, new RecordReviewDecisionRequest("rejected", Comment: "no"), Ct));
        Assert.Equal("invalid_request", anonymous.Code);
        var stranger = await Assert.ThrowsAsync<ReviewRequestException>(() =>
            service.RecordDecisionAsync(Repo, created.Id, new RecordReviewDecisionRequest("rejected", Reviewer: "intruder@example.com"), Ct));
        Assert.Equal("not_a_reviewer", stranger.Code);
        var unknownKind = await Assert.ThrowsAsync<ReviewRequestException>(() =>
            service.RecordDecisionAsync(Repo, created.Id, new RecordReviewDecisionRequest("maybe"), Ct));
        Assert.Equal("invalid_request", unknownKind.Code);

        var rejected = await service.RecordDecisionAsync(Repo, created.Id,
            new RecordReviewDecisionRequest("rejected", Reviewer: "Second@Example.com", Comment: "Rename the column first."), Ct);
        Assert.Equal(ReviewRequestStatus.Rejected, rejected.Status);
        var decision = Assert.Single(rejected.Decisions);
        Assert.Equal("Second@Example.com", decision.Reviewer);
        Assert.Null(decision.Trailer);
        Assert.Null(decision.VerifiedUtc);

        var approved = await service.RecordDecisionAsync(Repo, created.Id, new RecordReviewDecisionRequest("approved", Trailer: Trailer(tree)), Ct);
        Assert.Equal(ReviewRequestStatus.Approved, approved.Status);
        Assert.Equal(2, approved.Decisions.Count);
        Assert.Equal(ReviewRequestStatus.Approved, Assert.Single((await service.ListAsync(Repo, Ct)).Requests).Status);
    }

    [Fact]
    public async Task CancelledRequestsTakeNoDecisionsAndHideTheirApprovals()
    {
        await StageChangesAsync();
        var tree = (await Git("write-tree")).Trim();
        var service = Service(FakeCommitSignatureVerifier.ValidFor("owner@example.com"));
        var created = await service.CreateAsync(Repo, new CreateReviewRequest(Reviewers: ["owner@example.com"]), null, Ct);
        await service.RecordDecisionAsync(Repo, created.Id, new RecordReviewDecisionRequest("approved", Trailer: Trailer(tree)), Ct);
        Assert.Single(await _store.FindVerifiedApprovalsAsync(tree, Ct));

        var cancelled = await service.CancelAsync(Repo, created.Id, Ct);
        Assert.Equal(ReviewRequestStatus.Cancelled, cancelled.Status);
        Assert.Empty(await _store.FindVerifiedApprovalsAsync(tree, Ct));

        var closed = await Assert.ThrowsAsync<ReviewRequestException>(() =>
            service.RecordDecisionAsync(Repo, created.Id, new RecordReviewDecisionRequest("approved", Trailer: Trailer(tree)), Ct));
        Assert.Equal("not_open", closed.Code);
        Assert.Equal(ReviewRequestStatus.Cancelled, (await service.CancelAsync(Repo, created.Id, Ct)).Status);
    }

    [Fact]
    public async Task RequestsAreScopedToTheirProjectAndDeleteCascades()
    {
        await StageChangesAsync();
        var tree = (await Git("write-tree")).Trim();
        var service = Service(FakeCommitSignatureVerifier.ValidFor("owner@example.com"));
        var created = await service.CreateAsync(Repo, new CreateReviewRequest(Reviewers: ["owner@example.com"]), null, Ct);
        await service.RecordDecisionAsync(Repo, created.Id, new RecordReviewDecisionRequest("approved", Trailer: Trailer(tree)), Ct);

        var otherProject = Path.Combine(_directory, "elsewhere");
        Assert.Empty((await service.ListAsync(otherProject, Ct)).Requests);
        var foreign = await Assert.ThrowsAsync<ReviewRequestException>(() => service.GetAsync(otherProject, created.Id, Ct));
        Assert.Equal("not_found", foreign.Code);
        Assert.Equal(404, foreign.Status);
        var garbage = await Assert.ThrowsAsync<ReviewRequestException>(() => service.GetAsync(Repo, "not-a-guid", Ct));
        Assert.Equal("not_found", garbage.Code);
        // The project is the same with a trailing separator or a relative segment in the path.
        Assert.Single((await service.ListAsync(Repo + Path.DirectorySeparatorChar, Ct)).Requests);
        Assert.Single((await service.ListAsync(Path.Combine(Repo, "..", "repo"), Ct)).Requests);

        await service.DeleteAsync(Repo, created.Id, Ct);
        var gone = await Assert.ThrowsAsync<ReviewRequestException>(() => service.GetAsync(Repo, created.Id, Ct));
        Assert.Equal("not_found", gone.Code);
        Assert.Empty(await _store.GetDecisionsAsync(created.Id, Ct));
        Assert.Empty(await _store.FindVerifiedApprovalsAsync(tree, Ct));
    }

    private async Task AssertInvalidAsync(ReviewRequestService service, CreateReviewRequest request)
    {
        var error = await Assert.ThrowsAsync<ReviewRequestException>(() => service.CreateAsync(Repo, request, null, Ct));
        Assert.Equal("invalid_request", error.Code);
        Assert.Equal(400, error.Status);
    }

    private ReviewRequestService Service(ICommitSignatureVerifier verifier) =>
        new(_store, new GitStagedSnapshotProvider(), verifier);

    private async Task StageChangesAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(Repo, "db.cs"), StagedText, Ct);
        await Git("add", "--", "db.cs");
    }

    private static string Trailer(string tree) => HumanSignatureRuleTests.SignatureLine(tree);

    private async Task<string> Git(params string[] arguments)
    {
        var result = await GitCli.RunAsync(Repo, arguments, Ct, timeout: TimeSpan.FromSeconds(30));
        if (result.TimedOut || result.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed: {result.StdErr}");
        return result.StdOut;
    }

    private sealed class SwitchableVerifier : ICommitSignatureVerifier
    {
        public CommitSignatureVerification Answer { get; set; } = new(CommitSignatureStatus.Rejected, "not configured");
        public List<CommitSignature> Calls { get; } = [];

        public Task<CommitSignatureVerification> VerifyAsync(CommitSignature signature, CancellationToken cancellationToken)
        {
            Calls.Add(signature);
            return Task.FromResult(Answer);
        }
    }
}
