using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using VibeRails.Data.Sqlite;
using VibeRails.DB;
using VibeRails.Services.Git;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.VCA;
using VibeRails_Front.Data;
using VibeRails_Front.Data.Entities;
using VibeRails_Front.Data.Ownership;
using VibeRails_Front.Services;
using VibeRails_Front.Services.Auth0;
using VibeRails_Front.Services.FileStorage;
using Desktop = VibeRails.Services.ReviewRequests;
using Hosted = VibeRails_Front.Services.ReviewRequests;
using Xunit;

namespace BoardSync.Integration;

/// <summary>
/// VB-50 slice B: a staged change captured by the desktop (slice A) is stored by viberails.ai, read
/// and answered by a listed reviewer, and the approval the desktop polls lands in its commit-hook
/// ledger. The desktop transport is slice C, so the JSON handoff here is the desktop's own
/// serialization fed to the hosted services, as the routes would.
/// </summary>
public sealed class ReviewRequestRoundTripTests
{
    private const string Owner = "owner@example.test";
    private const string Reviewer = "reviewer@example.test";

    [Fact]
    public async Task StagedBundleToHostedReviewerDecisionAndDesktopLedger_RoundTripAcrossBothProducts()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "review-request-roundtrip-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        Directory.CreateDirectory(repo);
        using var signer = RSA.Create(2048);
        try
        {
            // Desktop: a real repository with a staged change and one finished session.
            async Task<string> Git(params string[] arguments)
            {
                var result = await GitCli.RunAsync(repo, arguments, ct, timeout: TimeSpan.FromSeconds(30));
                Assert.True(!result.TimedOut && result.ExitCode == 0, $"git {string.Join(' ', arguments)}: {result.StdErr}");
                return result.StdOut;
            }
            await Git("init", "-q");
            await Git("config", "user.email", "roundtrip@example.invalid");
            await Git("config", "user.name", "Round trip");
            await Git("config", "core.autocrlf", "false");
            await Git("config", "commit.gpgsign", "false");
            await File.WriteAllTextAsync(Path.Combine(repo, "retry.cs"), "class Retry { int Attempts = 1; }\n", ct);
            await File.WriteAllTextAsync(Path.Combine(repo, "old.md"), "# Removed later\n", ct);
            await Git("add", "-A", "--", ".");
            await Git("-c", "core.hooksPath=", "commit", "-q", "-m", "baseline");
            await File.WriteAllTextAsync(Path.Combine(repo, "retry.cs"), "class Retry { int Attempts = 3; /* <backoff> & 'jitter' */ }\n", ct);
            await File.WriteAllTextAsync(Path.Combine(repo, "added.cs"), "class Added { }\n", ct);
            // VIBE-211: a binary file travels as bytes and a new executable as its mode, so this request is approvable.
            byte[] logo = [0x89, 0x50, 0x4E, 0x47, 0x00, 0x00, .. Enumerable.Range(0, 256).Select(i => (byte)i), 0xFF];
            await File.WriteAllBytesAsync(Path.Combine(repo, "logo.png"), logo, ct);
            await File.WriteAllTextAsync(Path.Combine(repo, "run.sh"), "#!/bin/sh\necho run\n", ct);
            await Git("rm", "-q", "--", "old.md");
            await Git("add", "--", "retry.cs", "added.cs", "logo.png", "run.sh");
            await Git("update-index", "--chmod=+x", "run.sh");
            var tree = (await Git("write-tree")).Trim();

            var state = $"Data Source={Path.Combine(root, "state.db")};Pooling=False";
            var sessions = new Repository(state);
            var sessionId = Guid.NewGuid().ToString("D");
            await sessions.CreateSessionAsync(sessionId, "codex", null, repo, Environment.ProcessId);
            await sessions.LogSessionOutputAsync(sessionId, Encoding.UTF8.GetBytes("Review round trip\r\n"));
            await sessions.CompleteSessionAsync(sessionId, 0);

            var remote = await RemoteAsync(signer, ct);
            var ledger = new Desktop.ReviewRequestService(new ReviewRequestStore(state), new GitStagedSnapshotProvider(), new HostedVerifier(remote));
            var local = await ledger.CreateAsync(repo, new Desktop.CreateReviewRequest(
                Reviewers: ["Reviewer@Example.test"], Sessions: [sessionId], CardKey: "VB-OGK99-278", Note: "Check the retry bound."), sessions, ct);
            Assert.Equal(tree, local.TreeId);
            var bundle = (await ledger.GetAsync(repo, local.Id, ct)).Bundle;
            var capturedLogo = bundle.Files.Single(f => f.Path == "logo.png");
            Assert.Equal(("binary", null, Convert.ToBase64String(logo), "100644"), (capturedLogo.Omitted, capturedLogo.After, capturedLogo.AfterBytes, capturedLogo.Mode));
            Assert.Equal("100755", bundle.Files.Single(f => f.Path == "run.sh").Mode);
            // The disclosure the person confirms says that the bytes travel.
            var disclosure = await new Desktop.ReviewRequestSender(new ReviewRequestStore(state), ledger, null!, null!, null!, null!, () => null)
                .DisclosureAsync(repo, local.Id, sessions, ct);
            Assert.Equal((1, (long)logo.Length, true), (disclosure.FilesWithBytes, disclosure.CarriedBytes, disclosure.Files.Single(f => f.Path == "logo.png").BytesIncluded));

            // Hosted store: the desktop's own JSON for the bundle, bound the way MVC binds the route body.
            var body = $"{{\"requestId\":\"{local.Id}\",\"bundle\":{JsonSerializer.Serialize(bundle, Desktop.ReviewRequestJsonContext.Default.ReviewBundle)}}}";
            Hosted.ReviewRequestStatusDto hosted;
            await using (var db = remote.Db(23))
            {
                var (created, request) = await Service(db).CreateAsync(JsonSerializer.Deserialize<Hosted.CreateReviewRequestBody>(body, JsonSerializerOptions.Web), ct);
                Assert.True(created);
                hosted = request;
            }
            Assert.Equal((tree, Guid.Parse(local.Id), "open"), (hosted.TreeId, hosted.RequestId, hosted.Status));
            // Slice C: the desktop transport accepts the create answer for exactly this request and tree.
            var accepted = Desktop.ReviewRequestTransport.ParseStatus(JsonSerializer.SerializeToUtf8Bytes(hosted, JsonSerializerOptions.Web), local.Id, tree, null);
            Assert.Equal((hosted.Id, hosted.ExpiresUtc), (accepted.Id, accepted.ExpiresUtc));
            Assert.True(Desktop.ReviewRequestTransport.IsReference(hosted.Id));
            await using (var db = remote.Db(23)) // An exact retry of the same send stores nothing new.
                Assert.False((await Service(db).CreateAsync(JsonSerializer.Deserialize<Hosted.CreateReviewRequestBody>(body, JsonSerializerOptions.Web), ct)).Created);

            // The session's real archive reaches the requester's private storage through the existing
            // ingest (covered by its own tests); here it is the desktop encoder's actual bytes.
            using var exported = new MemoryStream();
            Assert.NotNull(await sessions.WriteSessionExportAsync(sessionId, exported, ct));
            using var compressed = new MemoryStream();
            await using (var brotli = new BrotliStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
                await brotli.WriteAsync(exported.ToArray(), ct);
            var blob = compressed.ToArray();
            await using (var db = remote.Db(23))
            {
                db.DataEnvelopes.Add(new DataEnvelope { UserId = 23, Kind = "session", SourceId = Guid.Parse(sessionId), SchemaVersion = 2,
                    ComputerName = "fixture", BlobName = "review-recording", Sha256 = Convert.ToHexStringLower(SHA256.HashData(blob)), CompressedBytes = blob.Length });
                await db.SaveChangesAsync(ct);
            }

            // Reviewer: reads exactly what the desktop captured, replays the session, sees the statement.
            await using (var db = remote.Db(25))
            {
                var view = await new Hosted.ReviewRequestAccess(db, TimeProvider.System).ReadAsync(hosted.Id, ct);
                Assert.NotNull(view);
                Assert.Equal(("open", Owner, "Check the retry bound.", true), (view.Status, view.RequestedBy, view.Note, view.CanDecide));
                Assert.Equal(Encoding.UTF8.GetString(CommitSignatureFormat.BuildPayload(tree)), view.Statement);
                Assert.Equal(bundle.Files.Select(f => (f.Path, f.Change, f.Before, f.After, f.Omitted, f.Mode, f.PreviousMode)),
                    view.Files.Select(f => (f.Path, f.Change, f.Before, f.After, f.Omitted, f.Mode, f.PreviousMode)));
                Assert.Equal("# Removed later\n", view.Files.Single(f => f.Path == "old.md").Before); // Deleted text from the base tree.
                // VIBE-211: the page learns the bytes travel and how many, never the base64 itself.
                var pageLogo = view.Files.Single(f => f.Path == "logo.png");
                Assert.Equal((null, (long)logo.Length), (pageLogo.AfterBytes, pageLogo.CarriedBytes));
                Assert.Equal("ready", Assert.Single(view.Sessions).Status);
                var replay = await new SessionEnvelopeReadService(db, new Storage(blob), NullLogger<SessionEnvelopeReadService>.Instance)
                    .OpenReviewReplayPayloadAsync(hosted.Id, 1, ct);
                Assert.Equal(SessionEnvelopeReadStatus.Ok, replay.Status);
                using (replay.Content)
                {
                    var payload = await JsonDocument.ParseAsync(replay.Content!, cancellationToken: ct);
                    Assert.Contains("UmV2aWV3IHJvdW5kIHRy", payload.RootElement.GetRawText()); // "Review round tr", base64.
                }
            }

            // Slice D: the reviewer's own VibeRails lists the request and reads the bundle with its key (the
            // hosted projections, through the desktop's validation), then proves in its own clone, which has
            // the base commit and never saw the staged change, that the files rebuild the tree it would sign.
            await using (var db = remote.Db(25))
            {
                var access = new Hosted.ReviewRequestAccess(db, TimeProvider.System);
                var inbox = Desktop.ReviewRequestTransport.ParseInbox(JsonSerializer.SerializeToUtf8Bytes(await access.InboxAsync(ct), JsonSerializerOptions.Web));
                var listed = Assert.Single(inbox.Requests!);
                Assert.Equal((hosted.Id, "repo", Owner, "open", "VB-OGK99-278", false), (listed.Id, listed.Repository, listed.RequestedBy, listed.Status, listed.CardKey, listed.Decided));
                // The key projection carries the bytes (VIBE-211); without them the binary file would make the request unapprovable.
                var read = Desktop.ReviewRequestTransport.ParseBundle(
                    JsonSerializer.SerializeToUtf8Bytes(await access.ReadAsync(hosted.Id, includeBytes: true, ct), JsonSerializerOptions.Web), hosted.Id);
                Assert.Equal((tree, true, Encoding.UTF8.GetString(CommitSignatureFormat.BuildPayload(tree))), (read.TreeId, read.CanDecide, read.Statement));
                Assert.Equal(Convert.ToBase64String(logo), read.Files!.Single(f => f.Path == "logo.png").AfterBytes);
                Assert.Empty(Desktop.ReviewTreeRebuilder.FilesWithoutContent(read.Files!));
                var pageOnly = Desktop.ReviewRequestTransport.ParseBundle(
                    JsonSerializer.SerializeToUtf8Bytes(await access.ReadAsync(hosted.Id, ct), JsonSerializerOptions.Web), hosted.Id);
                Assert.Equal(["logo.png"], Desktop.ReviewTreeRebuilder.FilesWithoutContent(pageOnly.Files!));

                var clone = Path.Combine(root, "reviewer-clone");
                await Git("clone", "-q", repo, clone);
                var rebuilt = await Desktop.ReviewTreeRebuilder.RebuildAsync(clone, read.TreeId, read.BaseTree, read.BaseCommit, read.Files!, ct);
                Assert.True(rebuilt.Matches, rebuilt.Message);
                Assert.Equal(tree, rebuilt.RebuiltTree);
                var tampered = read.Files!.Select(f => f.Path == "retry.cs" ? f with { After = f.After!.Replace("Attempts = 3", "Attempts = 30", StringComparison.Ordinal) } : f).ToList();
                var refused = await Desktop.ReviewTreeRebuilder.RebuildAsync(clone, read.TreeId, read.BaseTree, read.BaseCommit, tampered, ct);
                Assert.Equal("tree_mismatch", refused.Code);
                Assert.NotEqual(tree, refused.RebuiltTree);
            }

            // Reviewer answers: a rejection, then an approval signed in the desktop's trailer format.
            var trailer = CommitSignatureFormat.Format(new CommitSignature(remote.KeyId, remote.Fingerprint, tree,
                Convert.ToBase64String(signer.SignData(CommitSignatureFormat.BuildPayload(tree), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
            // Slice F: the rejection carries line comments; the host bounds and cleans them.
            await using (var db = remote.Db(25))
                await Service(db).DecideAsync(hosted.Id, new("rejected", null, "One more retry case.",
                    [new Hosted.ReviewLineComment("retry.cs", 1, "After", "Bound the attempts.\r\n\u0007"), new Hosted.ReviewLineComment("old.md", 1, "before", "Keep a stub.")]), "roundtrip", ct);
            // Slice F review F1: a maximal rejection (fifty comments filling the shared total budget with HTML-sensitive
            // text) is legitimate on the host; fifty of them, escaped the way the host's serializer escapes them, must
            // still fit what the desktop reads per poll.
            await using (var db = remote.Db(25))
                await Service(db).DecideAsync(hosted.Id, new("rejected", null, new string('<', Hosted.ReviewRequestContract.MaxCommentLength),
                    Enumerable.Range(1, Hosted.ReviewRequestContract.MaxLineComments).Select(i => new Hosted.ReviewLineComment(new string('<', 100), i, "after",
                        new string('<', Hosted.ReviewRequestContract.MaxLineCommentTotalChars / Hosted.ReviewRequestContract.MaxLineComments - 100))).ToList()), "roundtrip", ct);
            await using (var db = remote.Db(25))
                Assert.True((await Service(db).DecideAsync(hosted.Id, new("approved", trailer, "Looks right now."), "roundtrip", ct)).Created);

            // Desktop poll: the decisions route's JSON, serialized with the web defaults MVC uses (which escape every
            // non-ASCII or HTML-sensitive character), read the way the desktop would.
            Hosted.ReviewRequestStatusDto pollDto;
            await using (var db = remote.Db(23))
                pollDto = (await Service(db).DecisionsAsync(hosted.Id, ct))!;
            var pollJson = JsonSerializer.Serialize(pollDto, JsonSerializerOptions.Web);
            using var poll = JsonDocument.Parse(pollJson);
            Assert.Equal("approved", poll.RootElement.GetProperty("status").GetString());
            var decisions = poll.RootElement.GetProperty("decisions").EnumerateArray().ToList();
            Assert.Equal(["rejected", "rejected", "approved"], decisions.Select(d => d.GetProperty("decision").GetString()));
            Assert.Equal(["retry.cs", "old.md"], decisions[0].GetProperty("lineComments").EnumerateArray().Select(c => c.GetProperty("path").GetString()));
            var polledTrailer = decisions[2].GetProperty("trailer").GetString();
            Assert.Equal(trailer, polledTrailer);
            Assert.True(CommitSignatureFormat.TryParseTrailer(polledTrailer!, out var polledSignature));
            Assert.Equal(tree, polledSignature.TreeId);
            // Slice F review F1: the maximal decision, escaped, times the host's decision limit fits the desktop's poll bound.
            var maximalBytes = JsonSerializer.SerializeToUtf8Bytes(pollDto.Decisions[1], JsonSerializerOptions.Web).Length;
            Assert.Contains("\\u003C", pollJson);
            Assert.True((long)maximalBytes * Hosted.ReviewRequestContract.MaxDecisionsPerRequest <= Desktop.ReviewRequestTransport.MaxResponseBytes,
                $"{maximalBytes} escaped bytes per maximal decision; {Hosted.ReviewRequestContract.MaxDecisionsPerRequest} of them exceed the desktop poll bound");
            // Slice C: the same JSON through the desktop transport's validation, bound to this reference.
            var polled = Desktop.ReviewRequestTransport.ParseStatus(Encoding.UTF8.GetBytes(pollJson), null, null, hosted.Id);
            Assert.Equal("approved", polled.Status);
            Assert.Equal(["rejected", "rejected", "approved"], polled.Decisions!.Select(d => d.Decision));
            Assert.Equal(Hosted.ReviewRequestContract.MaxLineComments, polled.Decisions[1].LineComments!.Count);
            Assert.Equal(polledTrailer, polled.Decisions[2].Trailer);
            Assert.Equal(Reviewer, polled.Decisions[2].Reviewer, ignoreCase: true);
            Assert.Equal([new Desktop.ReviewLineComment("retry.cs", 1, "after", "Bound the attempts."), new Desktop.ReviewLineComment("old.md", 1, "before", "Keep a stub.")],
                polled.Decisions[0].LineComments);
            await using (var db = remote.Db(25)) // The reviewer sees their own line comments on the page.
                Assert.Equal(2, (await new Hosted.ReviewRequestAccess(db, TimeProvider.System).ReadAsync(hosted.Id, ct))!.YourDecisions[0].LineComments!.Count);

            // Both decisions land in the desktop ledger as the poller lands them: the rejection with its line
            // comments, the approval re-verified there so the commit hook can place it.
            var landedRejection = await ledger.RecordDecisionAsync(repo, local.Id, new Desktop.RecordReviewDecisionRequest(Decision: "rejected",
                Comment: polled.Decisions[0].Comment, Reviewer: polled.Decisions[0].Reviewer, LineComments: polled.Decisions[0].LineComments), ct);
            Assert.Equal(Desktop.ReviewRequestStatus.Rejected, landedRejection.Status);
            Assert.Equal(polled.Decisions[0].LineComments, Assert.Single(landedRejection.Decisions).LineComments);
            var landed = await ledger.RecordDecisionAsync(repo, local.Id, new Desktop.RecordReviewDecisionRequest(Decision: "approved", Trailer: polledTrailer), ct);
            Assert.Equal(Desktop.ReviewRequestStatus.Approved, landed.Status);
            Assert.Equal(2, landed.Decisions.Count);
            Assert.Equal(Reviewer, landed.Decisions[1].Reviewer, ignoreCase: true);
            Assert.Equal(polledTrailer, landed.Decisions[1].Trailer);
            Assert.Null(landed.Decisions[1].LineComments);

            // Cancelling ends the reviewer's access; the requester keeps the record.
            await using (var db = remote.Db(23))
                Assert.Equal("cancelled", (await Service(db).CancelAsync(hosted.Id, ct))!.Status);
            await using (var db = remote.Db(25))
            {
                Assert.Null(await new Hosted.ReviewRequestAccess(db, TimeProvider.System).ReadAsync(hosted.Id, ct));
                Assert.Equal(SessionEnvelopeReadStatus.NotFound, (await new SessionEnvelopeReadService(db, new Storage(blob),
                    NullLogger<SessionEnvelopeReadService>.Instance).OpenReviewReplayPayloadAsync(hosted.Id, 1, ct)).Status);
            }
            await using (var db = remote.Db(23))
                Assert.Equal(3, (await Service(db).DecisionsAsync(hosted.Id, ct))!.Decisions.Count);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(root, recursive: true); // Only the unique disposable fixture allocated above.
        }
    }

    private static readonly SigningRequestLimiter Limiter = new(TimeProvider.System);

    private static Hosted.ReviewRequestService Service(VibeRailsDbContext db) => new(db, TimeProvider.System,
        new Hosted.ReviewRequestAccess(db, TimeProvider.System), Signatures(db), Limiter, new Hosted.ReviewRequestWriteBudget());

    private static SigningKeyService Signatures(VibeRailsDbContext db)
        => new(db, new SigningKeyChallenges(TimeProvider.System), new NoDirectory());

    private sealed record Remote(Func<int?, VibeRailsDbContext> Db, Guid KeyId, string Fingerprint);

    /// <summary>A synthetic hosted database: requester 23 and listed reviewer 25 with a registered signing key.</summary>
    private static async Task<Remote> RemoteAsync(RSA signer, CancellationToken ct)
    {
        var remoteRoot = new InMemoryDatabaseRoot();
        var remoteName = "review-roundtrip-" + Guid.NewGuid().ToString("N");
        VibeRailsDbContext Db(int? owner)
        {
            var db = new VibeRailsDbContext(new DbContextOptionsBuilder<VibeRailsDbContext>().UseInMemoryDatabase(remoteName, remoteRoot).Options);
            if (owner is { } id) db.SetCurrentUser(id);
            return db;
        }
        var key = SigningKeyCrypto.ParsePublicKey(signer.ExportSubjectPublicKeyInfoPem());
        var signingKey = new SigningKey { UserId = 25, Name = "Reviewer laptop", Fingerprint = key.Fingerprint, PublicKeyPem = key.PublicKeyPem, KeySize = key.KeySize };
        await using (var db = Db(null))
        {
            db.Users.AddRange(new User { Id = 23, Auth0Id = "auth0|synthetic-requester", Email = Owner, EmailVerified = true, UpdatedAt = DateTime.UtcNow },
                new User { Id = 25, Auth0Id = "auth0|synthetic-reviewer", Email = Reviewer, EmailVerified = true, UpdatedAt = DateTime.UtcNow });
            using (db.BeginPrivilegedWrite(PrivilegedWrites.IdentityBootstrap)) await db.SaveChangesAsync(ct);
        }
        await using (var db = Db(25))
        {
            db.SigningKeys.Add(signingKey);
            await db.SaveChangesAsync(ct);
        }
        return new(Db, signingKey.Id, key.Fingerprint);
    }

    /// <summary>
    /// The desktop's verifier calls <c>POST /public/api/v1/signatures/verify</c>; here it calls the
    /// service behind that route on the same synthetic database, unbound like the anonymous endpoint.
    /// </summary>
    private sealed class HostedVerifier(Remote remote) : ICommitSignatureVerifier
    {
        public async Task<CommitSignatureVerification> VerifyAsync(CommitSignature signature, CancellationToken ct)
        {
            await using var db = remote.Db(null);
            var result = await Signatures(db).VerifyAsync(signature.KeyId, CommitSignatureFormat.BuildPayload(signature.TreeId),
                Convert.FromBase64String(signature.SignatureBase64), ct, signature.Fingerprint);
            return result.Valid ? new(CommitSignatureStatus.Valid, "valid", result.SignerEmail) : new(CommitSignatureStatus.Rejected, result.Reason);
        }
    }

    private sealed class NoDirectory : IAuth0UserDirectory
    {
        public Task<Auth0DirectoryUser?> GetUserAsync(string auth0Id, CancellationToken ct) => Task.FromResult<Auth0DirectoryUser?>(null);
    }

    private sealed class Storage(byte[] blob) : IFileStorageService
    {
        public Task<FileDownloadResult> OpenReadAsync(string name, CancellationToken ct) => Task.FromResult(new FileDownloadResult(FileDownloadStatus.Ok, name, new MemoryStream(blob, false), blob.Length));
        public Task<FileUploadResult> UploadAsync(FileUploadRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StagedBlocksResult> GetStagedBlocksAsync(StagedBlocksRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<StageBlockResult> StageBlockAsync(StageBlockRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<FileUploadResult> CommitBlocksAsync(CommitBlocksRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}
