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

    /// <summary>
    /// Codex review F3: the hook's snapshot has nothing to read for a deleted path and flags it
    /// binary; the bundle must still show the reviewer what was removed, read from the base tree.
    /// </summary>
    [Fact]
    public async Task DeletedFilesKeepTheirBaseTextAndBinariesStayBinary()
    {
        var image = new byte[32];
        image[0] = 0x89; image[1] = 0x50; image[2] = 0; image[3] = 0x0A;
        await File.WriteAllBytesAsync(Path.Combine(Repo, "logo.png"), image, Ct);
        await File.WriteAllTextAsync(Path.Combine(Repo, "deleted.cs"), "class Gone\n{\n}\n", Ct);
        await Git("add", "--", "logo.png", "deleted.cs");
        await Git("-c", "core.hooksPath=", "commit", "-q", "-m", "add files");
        await Git("rm", "-q", "--", "logo.png", "deleted.cs", "other.txt");
        var service = Service(FakeCommitSignatureVerifier.ValidFor("owner@example.com"));

        var created = await service.CreateAsync(Repo, new CreateReviewRequest(Reviewers: ["owner@example.com"]), null, Ct);

        var files = (await service.GetAsync(Repo, created.Id, Ct)).Bundle.Files;
        Assert.Equal(["deleted.cs", "logo.png", "other.txt"], files.Select(file => file.Path));
        Assert.All(files, file => Assert.Equal("deleted", file.Change));
        Assert.All(files, file => Assert.Null(file.After));

        Assert.False(files[0].Binary);
        Assert.Equal("class Gone\n{\n}\n", files[0].Before);
        Assert.Null(files[0].Omitted);
        Assert.Equal("other\n", files[2].Before);
        Assert.Null(files[2].Omitted);

        Assert.True(files[1].Binary);
        Assert.Null(files[1].Before);
        Assert.Equal(ReviewBundleOmission.Binary, files[1].Omitted);
    }

    /// <summary>Codex review N2: a deleted text file over the per-file bound is oversized, not binary.</summary>
    [Fact]
    public async Task DeletedOversizedTextIsLabelledOversizedNotBinary()
    {
        await File.WriteAllTextAsync(Path.Combine(Repo, "big.txt"), new string('x', ReviewBundleCapture.MaxFileContentBytes + 1), Ct);
        await Git("add", "--", "big.txt");
        await Git("-c", "core.hooksPath=", "commit", "-q", "-m", "add big");
        await Git("rm", "-q", "--", "big.txt");
        var service = Service(FakeCommitSignatureVerifier.ValidFor("owner@example.com"));

        var created = await service.CreateAsync(Repo, new CreateReviewRequest(Reviewers: ["owner@example.com"]), null, Ct);

        var file = Assert.Single((await service.GetAsync(Repo, created.Id, Ct)).Bundle.Files);
        Assert.Equal("deleted", file.Change);
        Assert.False(file.Binary);
        Assert.Equal(ReviewBundleOmission.Oversized, file.Omitted);
        Assert.Null(file.Before);
        Assert.Null(file.After);
    }

    /// <summary>
    /// Codex review F2: a cancel that lands while an approval is being verified must win. The
    /// decision is refused and nothing becomes eligible for the commit hook.
    /// </summary>
    [Fact]
    public async Task ApprovalVerifiedAfterCancellationIsRefused()
    {
        await StageChangesAsync();
        var tree = (await Git("write-tree")).Trim();
        var verifier = new GatedVerifier(new CommitSignatureVerification(CommitSignatureStatus.Valid, "valid", "owner@example.com"));
        var service = Service(verifier);
        var created = await service.CreateAsync(Repo, new CreateReviewRequest(Reviewers: ["owner@example.com"]), null, Ct);

        var approving = service.RecordDecisionAsync(Repo, created.Id, new RecordReviewDecisionRequest("approved", Trailer: Trailer(tree)), Ct);
        await verifier.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        var cancelled = await service.CancelAsync(Repo, created.Id, Ct);
        Assert.Equal(ReviewRequestStatus.Cancelled, cancelled.Status);
        verifier.Release.SetResult();

        var refused = await Assert.ThrowsAsync<ReviewRequestException>(() => approving);
        Assert.Equal("not_open", refused.Code);
        var after = await service.GetAsync(Repo, created.Id, Ct);
        Assert.Equal(ReviewRequestStatus.Cancelled, after.Request.Status);
        Assert.Empty(after.Request.Decisions);
        Assert.Empty(await _store.FindVerifiedApprovalsAsync(tree, Ct));
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

        // Slice F: line comments are bounded and cleaned, then stored with the decision.
        var tooMany = await Assert.ThrowsAsync<ReviewRequestException>(() => service.RecordDecisionAsync(Repo, created.Id,
            new RecordReviewDecisionRequest("rejected", Reviewer: "second@example.com", Comment: "no",
                LineComments: Enumerable.Range(1, ReviewRequestService.MaxLineComments + 1).Select(i => new ReviewLineComment("db.cs", i, "after", "x")).ToList()), Ct));
        Assert.Equal("invalid_request", tooMany.Code);
        var badSide = await Assert.ThrowsAsync<ReviewRequestException>(() => service.RecordDecisionAsync(Repo, created.Id,
            new RecordReviewDecisionRequest("rejected", Reviewer: "second@example.com", LineComments: [new ReviewLineComment("db.cs", 1, "left", "x")]), Ct));
        Assert.Equal("invalid_request", badSide.Code);
        var blank = await Assert.ThrowsAsync<ReviewRequestException>(() => service.RecordDecisionAsync(Repo, created.Id,
            new RecordReviewDecisionRequest("rejected", Reviewer: "second@example.com", LineComments: [new ReviewLineComment("db.cs", 1, "after", " \u0007 ")]), Ct));
        Assert.Equal("invalid_request", blank.Code);
        var tooLong = await Assert.ThrowsAsync<ReviewRequestException>(() => service.RecordDecisionAsync(Repo, created.Id,
            new RecordReviewDecisionRequest("rejected", Reviewer: "second@example.com", LineComments: Enumerable.Range(1, ReviewRequestService.MaxLineComments)
                .Select(i => new ReviewLineComment("db.cs", i, "after", new string('x', ReviewRequestService.MaxLineCommentTotalChars / ReviewRequestService.MaxLineComments))).ToList()), Ct));
        Assert.Equal("invalid_request", tooLong.Code);
        Assert.Empty((await service.GetAsync(Repo, created.Id, Ct)).Request.Decisions);

        var rejected = await service.RecordDecisionAsync(Repo, created.Id,
            new RecordReviewDecisionRequest("rejected", Reviewer: "Second@Example.com", Comment: "Rename the column first.",
                LineComments: [new ReviewLineComment("db.cs", 1, "Before", "Old name.\r\n\u202eStill"), new ReviewLineComment("db.cs", 1, "after", "New name.")]), Ct);
        Assert.Equal(ReviewRequestStatus.Rejected, rejected.Status);
        var decision = Assert.Single(rejected.Decisions);
        Assert.Equal("Second@Example.com", decision.Reviewer);
        Assert.Null(decision.Trailer);
        Assert.Null(decision.VerifiedUtc);
        Assert.Equal([new ReviewLineComment("db.cs", 1, "before", "Old name.\n Still"), new ReviewLineComment("db.cs", 1, "after", "New name.")], decision.LineComments);
        Assert.Equal(decision.LineComments, Assert.Single((await service.GetAsync(Repo, created.Id, Ct)).Request.Decisions).LineComments);

        var approved = await service.RecordDecisionAsync(Repo, created.Id, new RecordReviewDecisionRequest("approved", Trailer: Trailer(tree)), Ct);
        Assert.Equal(ReviewRequestStatus.Approved, approved.Status);
        Assert.Equal(2, approved.Decisions.Count);
        Assert.Null(approved.Decisions[1].LineComments);
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

    /// <summary>
    /// Slice E: the card shows what a request would capture before one exists, and suggests the
    /// approvers of the <c>Signed Approval</c> rules the staged paths touch. Nothing is stored.
    /// </summary>
    [Fact]
    public async Task PreviewListsStagedFilesWithLineCountsAndSuggestsApproversOfMatchedRules()
    {
        await StageChangesAsync();
        await File.WriteAllTextAsync(Path.Combine(Repo, "vc.rules.md"),
            "## Vibe Rails Rules\n- [STOP] Signed Approval('db.cs', 'Lead@Example.com, second@example.com')\n"
            + "- [WARN] Signed Approval('other.txt', 'nobody@example.com')\n", Ct);
        await Git("add", "--", "vc.rules.md");
        var tree = (await Git("write-tree")).Trim();
        var service = Service(FakeCommitSignatureVerifier.ValidFor("owner@example.com"));

        var preview = await service.PreviewAsync(Repo, Ct);

        Assert.Equal(tree, preview.TreeId);
        Assert.Equal("repo", preview.Repository);
        Assert.Equal(["db.cs", "vc.rules.md"], preview.Files.Select(file => file.Path));
        var db = preview.Files[0];
        Assert.Equal("modified", db.Change);
        Assert.Equal((1, 1), (db.Added, db.Removed));
        Assert.True(db.TextIncluded);
        var rules = preview.Files[1];
        Assert.Equal("added", rules.Change);
        Assert.Equal((3, 0), (rules.Added, rules.Removed)); // the heading and two rule lines
        Assert.Equal(2, preview.FilesWithText);
        Assert.Equal(0, preview.FilesWithoutText);
        Assert.True(preview.TextBytes > 0);
        // other.txt is not staged, so its rule suggests nobody; the approvers keep the rule's lowercase spelling.
        var rule = Assert.Single(preview.Rules);
        Assert.Equal("db.cs", rule.Path);
        Assert.Equal("vc.rules.md", rule.SourcePath);
        Assert.Equal(["db.cs"], rule.MatchedFiles);
        Assert.Equal(["lead@example.com", "second@example.com"], preview.SuggestedReviewers);
        Assert.Equal("email", preview.Audience);
        Assert.Contains("terminal output", preview.SessionDisclosure);
        Assert.Empty((await service.ListAsync(Repo, Ct)).Requests);

        await Git("reset", "-q", "--", "db.cs", "vc.rules.md");
        await Git("checkout", "-q", "--", "db.cs");
        var nothing = await Assert.ThrowsAsync<ReviewRequestException>(() => service.PreviewAsync(Repo, Ct));
        Assert.Equal("nothing_staged", nothing.Code);
    }

    /// <summary>Slice E: a linked commit whose tree a reviewer approved shows that approval, found the way the hook finds it.</summary>
    [Fact]
    public async Task CommitApprovalsMatchTheCommittedTreeOfAnApprovedRequest()
    {
        await StageChangesAsync();
        var tree = (await Git("write-tree")).Trim();
        var service = Service(FakeCommitSignatureVerifier.ValidFor("lead@example.com"));
        var created = await service.CreateAsync(Repo, new CreateReviewRequest(Reviewers: ["lead@example.com"]), null, Ct);
        await service.RecordDecisionAsync(Repo, created.Id, new RecordReviewDecisionRequest("approved", Trailer(tree)), Ct);
        await Git("-c", "core.hooksPath=", "commit", "-q", "-m", "approved change");
        var head = (await Git("rev-parse", "HEAD")).Trim();
        var baseline = (await Git("rev-parse", "HEAD~1")).Trim();

        var result = await service.CommitApprovalsAsync(Repo, [head, head[..10].ToUpperInvariant(), baseline, "0123456"], Ct);

        Assert.Equal(4, result.Commits.Count);
        Assert.Equal(tree, result.Commits[0].TreeId);
        var approval = Assert.Single(result.Commits[0].Approvals);
        Assert.Equal("lead@example.com", approval.Reviewer);
        Assert.Equal(created.Id, approval.RequestId);
        // A prefix resolves to the same commit; a commit of another tree has no approval; an unknown sha has no tree.
        Assert.Equal(head[..10], result.Commits[1].Sha);
        Assert.Equal(tree, result.Commits[1].TreeId);
        Assert.Single(result.Commits[1].Approvals);
        Assert.NotNull(result.Commits[2].TreeId);
        Assert.Empty(result.Commits[2].Approvals);
        Assert.Null(result.Commits[3].TreeId);
        Assert.Empty(result.Commits[3].Approvals);

        var invalid = await Assert.ThrowsAsync<ReviewRequestException>(() => service.CommitApprovalsAsync(Repo, ["not-a-sha"], Ct));
        Assert.Equal("invalid_request", invalid.Code);
        var tooMany = await Assert.ThrowsAsync<ReviewRequestException>(() => service.CommitApprovalsAsync(Repo,
            Enumerable.Range(0, ReviewRequestService.MaxCommitLookups + 1).Select(i => i.ToString("x7")).ToList(), Ct));
        Assert.Equal("invalid_request", tooMany.Code);
        Assert.Empty((await service.CommitApprovalsAsync(Repo, [], Ct)).Commits);
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

    /// <summary>Signals when verification starts and answers only once released, so a cancel can interleave.</summary>
    private sealed class GatedVerifier(CommitSignatureVerification answer) : ICommitSignatureVerifier
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<CommitSignatureVerification> VerifyAsync(CommitSignature signature, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return answer;
        }
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
