using VibeRails.DTOs;
using VibeRails.Services.ReviewRequests;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.VCA;

/// <summary>
/// The review approval popup (VB-50 slice D) with a fake console, key store, verifier and host: it
/// shows the request, refuses before any key prompt when the tree cannot be proven, and posts the
/// approval only after the signature is verified.
/// </summary>
public sealed class ReviewSignaturePopupTests
{
    private const string Reference = "rvw_0123456789abcdef0123456789abcdef";
    private static readonly string Tree = new('d', 40);
    private static readonly string BaseTree = new('e', 40);
    private static readonly string BaseCommit = new('f', 40);
    private static readonly DateTime Created = new(2026, 10, 24, 10, 0, 0, DateTimeKind.Utc);
    private const string Repo = @"C:\work\vibe-rails";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ApprovesAfterTheTreeIsProvenAndPostsTheVerifiedTrailer()
    {
        var signer = new FakeSigner(Key("Laptop"));
        var host = new FakeHost(Bundle());
        var output = new StringWriter();

        var exit = await Popup(output, new ScriptedConsole(secrets: ["correct horse battery"]), signer, Verifier("me@example.test"), host)
            .RunAsync(new ReviewSignaturePopupRequest(Reference, Repo), Ct);

        Assert.Equal(0, exit);
        Assert.Equal((signer.Keys[0].Id, "correct horse battery", Tree), Assert.Single(signer.Signed));
        var (reference, decision) = Assert.Single(host.Posted);
        Assert.Equal((Reference, "approved", null), (reference, decision.Decision, decision.Comment));
        Assert.True(CommitSignatureFormat.TryParseTrailer(decision.Trailer!, out var signature));
        Assert.Equal(Tree, signature.TreeId);
        Assert.Equal((Repo, Tree), Assert.Single(host.Rebuilt));
        var transcript = output.ToString();
        Assert.Contains("vibe-rails", transcript);
        Assert.Contains("dev@example.test", transcript);
        Assert.Contains("VIBE-208", transcript);
        Assert.Contains("M db.cs (1 -> 1 lines)", transcript);
        Assert.Contains("1) Implementation (codex, ready)", transcript);
        Assert.Contains(Tree, transcript);
        Assert.Contains($"commit {BaseCommit}", transcript);
        Assert.Contains($"https://viberails.ai/Reviews/requests/{Reference}", transcript);
        Assert.Contains("Verified. The files in the request rebuild", transcript);
        Assert.Contains("Verified: signed by me@example.test", transcript);
        Assert.Contains("Approval recorded on viberails.ai", transcript);
        Assert.DoesNotContain("correct horse battery", transcript);
        // The summary comes before the verdict, which comes before any key prompt.
        Assert.True(transcript.IndexOf("Files", StringComparison.Ordinal) < transcript.IndexOf("Rebuilding", StringComparison.Ordinal));
        Assert.True(transcript.IndexOf("Rebuilding", StringComparison.Ordinal) < transcript.IndexOf("Signing keys synced", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRequestersTextIsShownWithoutControlCharacters()
    {
        var bundle = Bundle() with
        {
            Note = "Please check\u001b[2J the retry\nSecond line\u001b]0;fake\a\n\n\n",
            CardKey = "VIBE-208\u202E",
            Files = [File("safe\u200Bwidth.cs", "added", after: "x")]
        };
        var output = new StringWriter();

        await Popup(output, new ScriptedConsole(secrets: [null]), new FakeSigner(Key("Laptop")), Verifier("me@example.test"), new FakeHost(bundle))
            .RunAsync(new ReviewSignaturePopupRequest(Reference, Repo), Ct);

        var transcript = output.ToString();
        Assert.DoesNotContain('\u001b', transcript);
        Assert.DoesNotContain('\a', transcript);
        Assert.DoesNotContain('\u202E', transcript);
        Assert.Contains(@"Please check\u001B[2J the retry", transcript);
        Assert.Contains(@"Second line\u001B]0;fake\u0007", transcript);
        Assert.Contains(@"VIBE-208\u202E", transcript);
        Assert.Contains(@"A safe\u200Bwidth.cs", transcript);
        Assert.Contains("may be an agent's", transcript);
    }

    [Fact]
    public async Task FilesWithoutContentStopBeforeTheRebuildAndAnyKey()
    {
        var bundle = Bundle() with { Files = [File("db.cs", "modified", after: "x"), File("logo.png", "added", binary: true, omitted: "binary")] };
        var signer = new FakeSigner(Key("Laptop"));
        var host = new FakeHost(bundle);
        var output = new StringWriter();

        var exit = await Popup(output, new ScriptedConsole(secrets: ["correct horse battery"]), signer, Verifier("me@example.test"), host)
            .RunAsync(new ReviewSignaturePopupRequest(Reference, Repo), Ct);

        Assert.Equal(1, exit);
        Assert.Equal(0, signer.ListCalls);
        Assert.Empty(host.Rebuilt);
        Assert.Empty(host.Posted);
        var transcript = output.ToString();
        Assert.Contains("1 file(s) are listed without their content", transcript);
        Assert.Contains("logo.png", transcript);
        Assert.Contains("cannot be approved", transcript);
    }

    [Fact]
    public async Task ATreeThatDoesNotRebuildStopsBeforeAnyKey()
    {
        var signer = new FakeSigner(Key("Laptop"));
        var host = new FakeHost(Bundle()) { Rebuild = new ReviewTreeRebuildResult(false, "tree_mismatch", "The files in the request do not rebuild the tree it asks you to sign.", new string('1', 40)) };

        var exit = await Popup(new StringWriter(), new ScriptedConsole(secrets: ["correct horse battery"]), signer, Verifier("me@example.test"), host)
            .RunAsync(new ReviewSignaturePopupRequest(Reference, Repo), Ct);

        Assert.Equal(1, exit);
        Assert.Equal(0, signer.ListCalls);
        Assert.Empty(host.Posted);
    }

    [Theory]
    [InlineData("cancelled", true)]
    [InlineData("expired", true)]
    [InlineData("open", false)]
    public async Task ClosedOrForeignRequestsAreNotSigned(string status, bool canDecide)
    {
        var signer = new FakeSigner(Key("Laptop"));
        var host = new FakeHost(Bundle() with { Status = status, CanDecide = canDecide });
        var output = new StringWriter();

        var exit = await Popup(output, new ScriptedConsole(secrets: ["correct horse battery"]), signer, Verifier("me@example.test"), host)
            .RunAsync(new ReviewSignaturePopupRequest(Reference, Repo), Ct);

        Assert.Equal(1, exit);
        Assert.Equal(0, signer.ListCalls);
        Assert.Empty(host.Rebuilt);
        Assert.Contains(canDecide ? $"This request is {status}" : "not a listed reviewer", output.ToString());
    }

    /// <summary>
    /// Codex review of slice D, F2: viberails.ai keeps taking decisions after the first one (several
    /// approvers, or an approval after a rejection), so another reviewer's decision does not close the popup.
    /// </summary>
    [Theory]
    [InlineData("approved")]
    [InlineData("rejected")]
    public async Task AnotherReviewersDecisionDoesNotCloseTheRequest(string status)
    {
        var host = new FakeHost(Bundle() with { Status = status });
        var output = new StringWriter();

        var exit = await Popup(output, new ScriptedConsole(secrets: ["correct horse battery"]), new FakeSigner(Key("Laptop")), Verifier("me@example.test"), host)
            .RunAsync(new ReviewSignaturePopupRequest(Reference, Repo), Ct);

        Assert.Equal(0, exit);
        Assert.Single(host.Posted);
        Assert.Contains($"Another reviewer already {status} this request", output.ToString());
    }

    [Fact]
    public async Task AnEarlierApprovalIsNotSignedAgain()
    {
        var earlier = new ReviewRequestHostedDecision(3, "me@example.test", "approved", null,
            CommitSignatureFormat.Format(new CommitSignature(Guid.NewGuid(), new string('c', 64), Tree, Convert.ToBase64String(new byte[256]))), Created, Created);
        var signer = new FakeSigner(Key("Laptop"));
        var output = new StringWriter();

        var exit = await Popup(output, new ScriptedConsole(secrets: ["correct horse battery"]), signer, Verifier("me@example.test"),
            new FakeHost(Bundle() with { YourDecisions = [earlier] })).RunAsync(new ReviewSignaturePopupRequest(Reference, Repo), Ct);

        Assert.Equal(1, exit);
        Assert.Equal(0, signer.ListCalls);
        Assert.Contains("already approved this request", output.ToString());
    }

    [Theory]
    [InlineData(CommitSignatureStatus.Rejected)]
    [InlineData(CommitSignatureStatus.Unavailable)]
    public async Task ASignatureTheServerDoesNotConfirmIsNotPosted(CommitSignatureStatus status)
    {
        var host = new FakeHost(Bundle());

        var exit = await Popup(new StringWriter(), new ScriptedConsole(secrets: ["correct horse battery"]), new FakeSigner(Key("Laptop")),
            new FakeVerifier(new CommitSignatureVerification(status, "nope")), host).RunAsync(new ReviewSignaturePopupRequest(Reference, Repo), Ct);

        Assert.Equal(1, exit);
        Assert.Empty(host.Posted);
    }

    [Fact]
    public async Task TheHostsRefusalIsReportedAndTheExitIsNonZero()
    {
        var host = new FakeHost(Bundle()) { PostFailure = new ReviewRequestTransportException("signer_mismatch", "The signing key belongs to another viberails.ai account.", 409) };
        var output = new StringWriter();

        var exit = await Popup(output, new ScriptedConsole(secrets: ["correct horse battery"]), new FakeSigner(Key("Laptop")), Verifier("me@example.test"), host)
            .RunAsync(new ReviewSignaturePopupRequest(Reference, Repo), Ct);

        Assert.Equal(1, exit);
        Assert.Contains("belongs to another viberails.ai account", output.ToString());
        Assert.Contains("Your approval was not recorded", output.ToString());
    }

    [Fact]
    public async Task AKeyFromAnotherAccountIsWarnedAboutBeforeTheHostDecides()
    {
        var host = new FakeHost(Bundle());
        var output = new StringWriter();

        var exit = await Popup(output, new ScriptedConsole(secrets: ["correct horse battery"]), new FakeSigner(Key("Laptop")), Verifier("other@example.test"), host)
            .RunAsync(new ReviewSignaturePopupRequest(Reference, Repo), Ct);

        Assert.Equal(0, exit);
        Assert.Contains("Your saved viberails.ai account is me@example.test, and this key belongs to other@example.test", output.ToString());
        Assert.Single(host.Posted);
    }

    [Fact]
    public async Task AFetchFailureOrABadReferenceSignsNothing()
    {
        var signer = new FakeSigner(Key("Laptop"));
        var host = new FakeHost(Bundle()) { FetchFailure = new ReviewRequestTransportException("not_found", "viberails.ai has no open review request with this reference for your account.", 404) };
        var output = new StringWriter();

        Assert.Equal(1, await Popup(output, new ScriptedConsole(), signer, Verifier("me@example.test"), host).RunAsync(new ReviewSignaturePopupRequest(Reference, Repo), Ct));
        Assert.Contains("no open review request with this reference", output.ToString());
        Assert.Equal(1, await Popup(output, new ScriptedConsole(), signer, Verifier("me@example.test"), host).RunAsync(new ReviewSignaturePopupRequest("rvw_x", Repo), Ct));
        Assert.Equal(0, signer.ListCalls);
    }

    [Fact]
    public async Task RefusesToRunOutsideThePopupWindow()
    {
        var output = new StringWriter();
        var invocation = new VcaHookCommandParser().Parse(["--vca-hook", "sign-review", "--review", Reference, "--workdir", Repo]);

        var exit = await ReviewSignaturePopup.RunInConsoleAsync(invocation, output, VcaConsoleStyle.Plain, Ct);

        Assert.Equal(1, exit);
        Assert.Contains("only runs in the popup window", output.ToString());
    }

    [Fact]
    public void ParserReadsThePopupArguments()
    {
        var invocation = new VcaHookCommandParser().Parse(ConsoleReviewSignaturePopupLauncher.Arguments(Reference, Repo).ToArray());

        Assert.Equal(VcaHookKind.SignReview, invocation.Kind);
        Assert.Equal((Reference, Repo, true, false), (invocation.ReviewReference, invocation.WorkingDirectory, invocation.ShowConsoleWindow, invocation.ConsoleWindowAttached));
        Assert.Null(invocation.Signing);
    }

    /// <summary>The counts come from the carried text (which the rebuild verifies), never from the requester's changedLines.</summary>
    [Fact]
    public void FileLinesNameRenamesCountsAndOmissions()
    {
        Assert.Equal("R old.cs -> new.cs (2 -> 3 lines)", ReviewSignaturePopup.DescribeFile(new ReviewBundleFile("new.cs", "renamed", false, "old.cs", 0, "a\nb\n", "a\nb\nc", null)));
        Assert.Equal("A logo.png (no content: binary)", ReviewSignaturePopup.DescribeFile(new ReviewBundleFile("logo.png", "added", true, null, null, null, null, "binary")));
        Assert.Equal("D gone.md (1 -> 0 lines)", ReviewSignaturePopup.DescribeFile(new ReviewBundleFile("gone.md", "deleted", false, null, 99, "x", null, null)));
        Assert.Equal("A new.txt (0 -> 2 lines)", ReviewSignaturePopup.DescribeFile(new ReviewBundleFile("new.txt", "added", false, null, null, null, "one\r\ntwo\r\n", null)));
    }

    private static ReviewHostedBundle Bundle() => new(
        Reference, "vibe-rails", "dev@example.test", "open", Tree, BaseTree, BaseCommit, "VIBE-208", "Please check the retry path.",
        Created, Created.AddMonths(1), $"VibeRails human-verified commit v1\ntree {Tree}\n", true,
        [File("db.cs", "modified", after: "class Db { int Version = 2; }\n", before: "class Db { }\n", lines: 1)],
        [new ReviewHostedSession(1, "Implementation", "codex", Created, Created.AddHours(1), "ready")],
        []);

    private static ReviewBundleFile File(string path, string change, string? after = null, string? before = null, int? lines = 1, bool binary = false, string? omitted = null) =>
        new(path, change, binary, null, omitted is null ? lines : null, omitted is null ? before : null, omitted is null ? after : null, omitted);

    private static ReviewSignaturePopup Popup(TextWriter output, IHumanSignatureConsole console, ICommitTreeSigner signer, ICommitSignatureVerifier verifier, FakeHost host) =>
        new(output, console, signer, verifier, host.FetchAsync, host.RebuildAsync, host.PostAsync, "me@example.test", VcaConsoleStyle.Plain);

    private static FakeVerifier Verifier(string email) => new(new CommitSignatureVerification(CommitSignatureStatus.Valid, "valid", email));

    private static SigningKeyInfo Key(string name) =>
        new(Guid.NewGuid(), name, "-----BEGIN PUBLIC KEY-----", Convert.ToHexStringLower(Guid.NewGuid().ToByteArray()) + new string('0', 32),
            DateTimeOffset.UtcNow, Guid.NewGuid(), DateTimeOffset.UtcNow);

    private sealed class FakeHost(ReviewHostedBundle bundle)
    {
        public ReviewTreeRebuildResult Rebuild { get; init; } = new(true, "matches", $"The files in the request rebuild tree {bundle.TreeId} from base {bundle.BaseCommit}.", bundle.TreeId);
        public ReviewRequestTransportException? FetchFailure { get; init; }
        public ReviewRequestTransportException? PostFailure { get; init; }
        public List<(string Repository, string Tree)> Rebuilt { get; } = [];
        public List<(string Reference, ReviewInboxDecisionBody Decision)> Posted { get; } = [];

        public Task<ReviewHostedBundle> FetchAsync(string reference, CancellationToken ct) =>
            FetchFailure is null ? Task.FromResult(bundle) : Task.FromException<ReviewHostedBundle>(FetchFailure);

        public Task<ReviewTreeRebuildResult> RebuildAsync(string repository, ReviewHostedBundle request, CancellationToken ct)
        {
            Rebuilt.Add((repository, request.TreeId));
            return Task.FromResult(Rebuild);
        }

        public Task<ReviewRequestHostedDecision> PostAsync(string reference, ReviewInboxDecisionBody decision, CancellationToken ct)
        {
            if (PostFailure is not null) return Task.FromException<ReviewRequestHostedDecision>(PostFailure);
            Posted.Add((reference, decision));
            return Task.FromResult(new ReviewRequestHostedDecision(11, "me@example.test", decision.Decision, decision.Comment, decision.Trailer, Created.AddDays(1), Created.AddDays(1)));
        }
    }

    private sealed class FakeVerifier(CommitSignatureVerification result) : ICommitSignatureVerifier
    {
        public Task<CommitSignatureVerification> VerifyAsync(CommitSignature signature, CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class ScriptedConsole(IEnumerable<string?>? lines = null, IEnumerable<string?>? secrets = null) : IHumanSignatureConsole
    {
        private readonly Queue<string?> _lines = new(lines ?? []);
        private readonly Queue<string?> _secrets = new(secrets ?? []);

        public Task<string?> ReadLineAsync(string prompt, CancellationToken cancellationToken) =>
            Task.FromResult(_lines.Count > 0 ? _lines.Dequeue() : null);

        public Task<string?> ReadSecretAsync(string prompt, CancellationToken cancellationToken) =>
            Task.FromResult(_secrets.Count > 0 ? _secrets.Dequeue() : null);
    }

    private sealed class FakeSigner(params SigningKeyInfo[] keys) : ICommitTreeSigner
    {
        public SigningKeyInfo[] Keys { get; } = keys;
        public int ListCalls { get; private set; }
        public List<(Guid KeyId, string Password, string TreeId)> Signed { get; } = [];

        public Task<IReadOnlyList<SigningKeyInfo>> ListKeysAsync(CancellationToken cancellationToken)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<SigningKeyInfo>>(Keys);
        }

        public Task<CommitSignature> SignAsync(Guid keyId, string password, string treeId, CancellationToken cancellationToken)
        {
            if (password != "correct horse battery") throw new ArgumentException("Incorrect password or damaged key file.");
            Signed.Add((keyId, password, treeId));
            var key = Keys.Single(candidate => candidate.Id == keyId);
            return Task.FromResult(new CommitSignature(key.CloudKeyId!.Value, new string('c', 64), treeId, Convert.ToBase64String(new byte[512])));
        }
    }
}
