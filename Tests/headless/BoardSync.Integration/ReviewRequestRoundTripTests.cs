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
            await Git("rm", "-q", "--", "old.md");
            await Git("add", "--", "retry.cs", "added.cs");
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
                Assert.Equal(bundle.Files.Select(f => (f.Path, f.Change, f.Before, f.After, f.Omitted)),
                    view.Files.Select(f => (f.Path, f.Change, f.Before, f.After, f.Omitted)));
                Assert.Equal("# Removed later\n", view.Files.Single(f => f.Path == "old.md").Before); // Deleted text from the base tree.
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

            // Reviewer answers: a rejection, then an approval signed in the desktop's trailer format.
            var trailer = CommitSignatureFormat.Format(new CommitSignature(remote.KeyId, remote.Fingerprint, tree,
                Convert.ToBase64String(signer.SignData(CommitSignatureFormat.BuildPayload(tree), HashAlgorithmName.SHA256, RSASignaturePadding.Pss))));
            await using (var db = remote.Db(25))
                await Service(db).DecideAsync(hosted.Id, new("rejected", null, "One more retry case."), "roundtrip", ct);
            await using (var db = remote.Db(25))
                Assert.True((await Service(db).DecideAsync(hosted.Id, new("approved", trailer, "Looks right now."), "roundtrip", ct)).Created);

            // Desktop poll: the decisions route's JSON, read the way the desktop would.
            string pollJson;
            await using (var db = remote.Db(23))
                pollJson = JsonSerializer.Serialize(await Service(db).DecisionsAsync(hosted.Id, ct), JsonSerializerOptions.Web);
            using var poll = JsonDocument.Parse(pollJson);
            Assert.Equal("approved", poll.RootElement.GetProperty("status").GetString());
            var decisions = poll.RootElement.GetProperty("decisions").EnumerateArray().ToList();
            Assert.Equal(["rejected", "approved"], decisions.Select(d => d.GetProperty("decision").GetString()));
            var polledTrailer = decisions[1].GetProperty("trailer").GetString();
            Assert.Equal(trailer, polledTrailer);
            Assert.True(CommitSignatureFormat.TryParseTrailer(polledTrailer!, out var polledSignature));
            Assert.Equal(tree, polledSignature.TreeId);

            // The approval lands in the desktop ledger, re-verified there, so the commit hook can place it.
            var landed = await ledger.RecordDecisionAsync(repo, local.Id, new Desktop.RecordReviewDecisionRequest(Decision: "approved", Trailer: polledTrailer), ct);
            Assert.Equal(Desktop.ReviewRequestStatus.Approved, landed.Status);
            Assert.Equal(Reviewer, Assert.Single(landed.Decisions).Reviewer, ignoreCase: true);
            Assert.Equal(polledTrailer, landed.Decisions[0].Trailer);

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
                Assert.Equal(2, (await Service(db).DecisionsAsync(hosted.Id, ct))!.Decisions.Count);
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
