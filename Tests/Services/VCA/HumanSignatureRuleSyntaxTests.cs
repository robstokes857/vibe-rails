using System.Text;
using VibeRails.Services;
using VibeRails.Services.GitPreflight;
using VibeRails.Services.Mcp.Tools;
using VibeRails.Services.VCA;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.VCA;

public sealed class HumanSignatureRuleSyntaxTests
{
    private static readonly Guid KeyId = Guid.Parse("8b3f6a52-6c43-4f6e-9a1e-2d7f1c0b9e44");
    private static readonly string Fingerprint = new('c', 64);
    private static readonly string Tree = new('d', 40);
    private static readonly string Signature = Convert.ToBase64String(Enumerable.Repeat((byte)9, 512).ToArray());

    [Theory]
    [InlineData("Signed Approval('db.cs', 'owner@example.com')", "db.cs", "owner@example.com")]
    [InlineData("  signed approval ( \"src/db.cs\" , \"Owner@Example.com\" )  ", "src/db.cs", "owner@example.com")]
    [InlineData("Signed Approval('data', 'a@example.com, B@example.com, a@example.com')", "data", "a@example.com,b@example.com")]
    [InlineData("Signed Approval('.', 'a@sub.example.co.uk')", ".", "a@sub.example.co.uk")]
    [InlineData("Signed Approval('it\"s.txt', 'a@example.com')", "it\"s.txt", "a@example.com")]
    public void SignedApproval_ParsesPathAndApprovers(string text, string path, string approvers)
    {
        Assert.True(HumanSignatureRule.TryParseSignedApproval(text, out var approval));
        Assert.Equal(path, approval.RelativePath);
        Assert.Equal(approvers.Split(','), approval.Approvers);
        Assert.True(HumanSignatureRule.LooksLikeSignedApproval(text));
    }

    [Theory]
    [InlineData("Signed Approval")]
    [InlineData("Signed Approval()")]
    [InlineData("Signed Approval('db.cs')")]
    [InlineData("Signed Approval('db.cs', '')")]
    [InlineData("Signed Approval('', 'a@example.com')")]
    [InlineData("Signed Approval('db.cs', 'not-an-email')")]
    [InlineData("Signed Approval('db.cs', 'a@localhost')")]
    [InlineData("Signed Approval('db.cs', 'a@example.com,,b@example.com')")]
    [InlineData("Signed Approval('db.cs', 'a b@example.com')")]
    [InlineData("Signed Approval('db.cs', 'a@example.com') extra")]
    [InlineData("Signed Approval('db.cs', 'a@example.com', 'b@example.com')")]
    [InlineData("Signed Approval('db.cs', 'a@example.com')\n## Injected")]
    [InlineData("Signed Approval('db.cs' 'a@example.com')")]
    public void SignedApproval_RejectsMalformedText(string text)
    {
        Assert.False(HumanSignatureRule.TryParseSignedApproval(text, out _));
        Assert.True(HumanSignatureRule.LooksLikeSignedApproval(text));
    }

    [Fact]
    public void SignedApproval_CapsTheApproverList()
    {
        var eleven = string.Join(", ", Enumerable.Range(1, 11).Select(i => $"a{i}@example.com"));
        Assert.False(HumanSignatureRule.TryParseSignedApproval($"Signed Approval('db.cs', '{eleven}')", out _));
    }

    [Fact]
    public void HumanVerified_MatchesTheCatalogNameOnly()
    {
        Assert.True(HumanSignatureRule.IsHumanVerified("  human VERIFIED "));
        Assert.False(HumanSignatureRule.IsHumanVerified("Human verified please"));
        Assert.False(HumanSignatureRule.IsHumanVerified(null));
    }

    [Fact]
    public void Catalog_ListsBothRulesWithDescriptionsAndParsesThem()
    {
        var service = new RulesService();
        var entries = service.AllowedRulesWithDescriptions();
        Assert.Contains(entries, entry => entry.Name == HumanSignatureRule.HumanVerifiedName && entry.Description.Contains("Git Guard popup"));
        Assert.Contains(entries, entry => entry.Name == HumanSignatureRule.SignedApprovalTemplate && entry.Description.Contains("approver"));

        Assert.True(service.TryParse("Human verified", out var human));
        Assert.Equal(Rule.HumanVerified, human);
        Assert.True(service.TryParse("Signed Approval('db.cs', 'owner@example.com')", out var approval));
        Assert.Equal(Rule.SignedApproval, approval);
        Assert.False(service.TryParse("Signed Approval('db.cs')", out _));
    }

    [Fact]
    public void Trailer_RoundTripsAndNormalizesHexCase()
    {
        var signature = new CommitSignature(KeyId, Fingerprint, Tree, Signature);
        var line = CommitSignatureFormat.Format(signature);
        Assert.StartsWith("vibe-signature: v1 key=8b3f6a52-6c43-4f6e-9a1e-2d7f1c0b9e44 fp=", line);

        Assert.Equal(signature, Assert.Single(CommitSignatureFormat.Parse($"Subject\n\nBody\n\n{line}\n")));
        Assert.Equal(signature, Assert.Single(CommitSignatureFormat.Parse(
            $"Subject\r\n\r\n  VIBE-SIGNATURE:   V1 tree={Tree.ToUpperInvariant()} sig={Signature} fp={Fingerprint.ToUpperInvariant()} key={KeyId:D}  \r\n")));
    }

    [Theory]
    [InlineData("vibe-signature: v2 key={0} fp={1} tree={2} sig={3}")]
    [InlineData("vibe-signature: v1 key={0} fp={1} tree={2}")]
    [InlineData("vibe-signature: v1 key={0} fp={1} tree={2} sig={3} extra=1")]
    [InlineData("vibe-signature: v1 key={0} key={0} tree={2} sig={3}")]
    [InlineData("vibe-signature: v1 key=00000000-0000-0000-0000-000000000000 fp={1} tree={2} sig={3}")]
    [InlineData("vibe-signature: v1 key={0} fp=abc fp2={1} tree={2} sig={3}")]
    [InlineData("vibe-signature: v1 key={0} fp={1} tree=HEAD sig={3}")]
    [InlineData("vibe-signature: v1 key={0} fp={1} tree={2} sig=AAAA")]
    [InlineData("vibe-signature: v1 key={0} fp={1} tree={2} sig=%%%%")]
    [InlineData("see vibe-signature: v1 key={0} fp={1} tree={2} sig={3}")]
    public void Trailer_IgnoresMalformedLines(string template)
    {
        var line = string.Format(template, KeyId, Fingerprint, Tree, Signature);
        Assert.Empty(CommitSignatureFormat.Parse("Subject\n\n" + line));
    }

    [Fact]
    public void Trailer_DeduplicatesAndBoundsTheWorkOneMessageCanCause()
    {
        var lines = Enumerable.Range(0, 8)
            .Select(i => CommitSignatureFormat.Format(new CommitSignature(KeyId, Fingerprint, Tree,
                Convert.ToBase64String(Enumerable.Repeat((byte)i, 256).ToArray()))))
            .ToList();
        lines.Insert(1, lines[0]);
        var parsed = CommitSignatureFormat.Parse(string.Join("\n", lines));
        Assert.Equal(CommitSignatureFormat.MaxSignaturesPerMessage, parsed.Count);
        Assert.Equal(parsed.Count, parsed.Select(signature => signature.SignatureBase64).Distinct().Count());
    }

    [Fact]
    public void Payload_IsDomainSeparatedTreeStatement()
    {
        Assert.Equal("VibeRails human-verified commit v1\ntree " + Tree + "\n",
            Encoding.UTF8.GetString(CommitSignatureFormat.BuildPayload(Tree)));
        Assert.True(CommitSignatureFormat.IsObjectId(new string('a', 64)));
        Assert.False(CommitSignatureFormat.IsObjectId(new string('a', 39)));
        Assert.False(CommitSignatureFormat.IsObjectId(new string('g', 40)));
    }

    [Theory]
    [InlineData("WARN")]
    [InlineData("STOP")]
    public async Task MalformedSignedApprovalWarnsWithoutBlockingEveryCommit(string level)
    {
        var snapshot = Snapshot($"## Vibe Rails Rules\n- Signed Approval('db.cs') ({level})\n", "db.cs");
        foreach (var checkMessage in new[] { false, true })
        {
            var report = await RulesTool.ValidateVcaReportAsync(new GitStagedSnapshotProvider(), commitMessage: "Edit",
                validateCommitMessage: checkMessage, stagedSnapshot: snapshot,
                signatureVerifier: new ThrowingVerifier(), cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(report.HasStopViolation, report.Output);
            var finding = Assert.Single(report.Findings);
            Assert.Equal(VcaRuleFindingKind.Warning, finding.Kind);
            Assert.Contains("UNSUPPORTED: invalid signed approval", finding.Reason);
        }
    }

    [Fact]
    public async Task ApprovalPathThatReadsLikeAnotherRuleIsNotMisrouted()
    {
        // "test coverage" and "cyclomatic complexity" are substring triggers for other validators.
        var snapshot = Snapshot(
            "## Vibe Rails Rules\n- Signed Approval('docs/test coverage cyclomatic complexity.md', 'a@example.com') (STOP)\n",
            "other.txt");
        var report = await RulesTool.ValidateVcaReportAsync(new GitStagedSnapshotProvider(), stagedSnapshot: snapshot,
            signatureVerifier: new ThrowingVerifier(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(report.HasStopViolation, report.Output);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public async Task ApprovalPathMayNotEscapeItsDirectory()
    {
        var snapshot = Snapshot("## Vibe Rails Rules\n- Signed Approval('../db.cs', 'a@example.com') (STOP)\n", "db.cs");
        var report = await RulesTool.ValidateVcaReportAsync(new GitStagedSnapshotProvider(), stagedSnapshot: snapshot,
            signatureVerifier: new ThrowingVerifier(), cancellationToken: TestContext.Current.CancellationToken);
        Assert.False(report.HasStopViolation, report.Output);
        Assert.Contains("approval path may not escape", Assert.Single(report.Findings).Reason);
    }

    [Fact]
    public async Task MissingSignatureCarriesWhatThePopupNeeds()
    {
        var snapshot = Snapshot("## Vibe Rails Rules\n- Signed Approval('db.cs', 'Owner@example.com') (STOP)\n", "db.cs");
        var report = await RulesTool.ValidateVcaReportAsync(new GitStagedSnapshotProvider(), commitMessage: "Edit db",
            validateCommitMessage: true, stagedSnapshot: snapshot, signatureVerifier: new ThrowingVerifier(),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(report.HasStopViolation);
        var finding = Assert.Single(report.Findings);
        Assert.Equal(VcaRuleFindingKind.Blocked, finding.Kind);
        Assert.Equal(Tree, finding.SignatureRequest!.TreeId);
        Assert.Equal(["owner@example.com"], finding.SignatureRequest.Approvers);
        Assert.Contains("no vibe-signature: line", finding.Reason);
    }

    private static GitStagedSnapshot Snapshot(string rules, string changedPath)
    {
        var root = Path.Combine(Path.GetTempPath(), "human-signature-syntax-tests");
        return new GitStagedSnapshot(
            root,
            [new GitStagedFileSnapshot(changedPath, Path.Combine(root, changedPath), GitStagedChangeKind.Modified,
                ExistsInIndex: true, IsBinary: false, ChangedLineCount: 1, Content: "changed")],
            [new GitIndexTextFile("vc.rules.md", rules)],
            StagedIdentity: new GitStagedSnapshotIdentity(new string('e', 40), new string('f', 40), Tree));
    }

    /// <summary>These cases must be decided without asking viberails.ai.</summary>
    private sealed class ThrowingVerifier : ICommitSignatureVerifier
    {
        public Task<CommitSignatureVerification> VerifyAsync(CommitSignature signature, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The verifier must not be called for this case.");
    }
}
