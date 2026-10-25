using VibeRails.Routes;
using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Routes;

public sealed class HookRoutesVcaOverviewTests
{
    [Fact]
    public void BuildVcaValidationOverview_MapsFindingKindsAndCounts()
    {
        var summary = new VcaHookValidationSummary(
            HasError: false,
            HasStopViolation: true,
            HasCommitViolations: true,
            RequiredAcknowledgments: ["[VCA:vc.rules.md:package-changes]"],
            StagedFileCount: 3,
            ApplicableRuleCount: 4,
            Findings:
            [
                Finding(VcaRuleFindingKind.Blocked, "STOP"),
                Finding(VcaRuleFindingKind.AcknowledgmentRequired, "COMMIT", "[VCA:vc.rules.md:package-changes]"),
                Finding(VcaRuleFindingKind.Warning, "WARN"),
                Finding(VcaRuleFindingKind.Deferred, "STOP")
            ]);

        var response = HookRoutes.BuildVcaValidationOverview(summary);

        Assert.Equal("blocked", response.Outcome);
        Assert.Equal(3, response.StagedFileCount);
        Assert.Equal(4, response.ApplicableRuleCount);
        Assert.Equal(4, response.FindingCount);
        Assert.Equal(1, response.StopCount);
        Assert.Equal(1, response.CommitCount);
        Assert.Equal(1, response.WarningCount);
        Assert.Equal(1, response.DeferredCount);
        Assert.Collection(
            response.Findings,
            finding => Assert.Equal("blocked", finding.Status),
            finding => Assert.Equal("acknowledgment_required", finding.Status),
            finding => Assert.Equal("warning", finding.Status),
            finding => Assert.Equal("deferred", finding.Status));
    }

    [Fact]
    public void BuildVcaValidationOverview_DistinguishesEmptyAndAttentionOutcomes()
    {
        var empty = HookRoutes.BuildVcaValidationOverview(new VcaHookValidationSummary(
            HasError: false,
            HasStopViolation: false,
            HasCommitViolations: false,
            RequiredAcknowledgments: []));
        var attention = HookRoutes.BuildVcaValidationOverview(new VcaHookValidationSummary(
            HasError: false,
            HasStopViolation: false,
            HasCommitViolations: false,
            RequiredAcknowledgments: [],
            StagedFileCount: 1,
            ApplicableRuleCount: 1,
            Findings: [Finding(VcaRuleFindingKind.Warning, "WARN")]));

        Assert.Equal("empty", empty.Outcome);
        Assert.Equal("attention", attention.Outcome);
    }

    /// <summary>
    /// VB-50 slice E: a deferred signature finding names the reviewer whose stored approval over the
    /// staged tree the commit hook will apply, through the hook's own lookup, with the rule's approvers.
    /// </summary>
    [Fact]
    public async Task WithStoredApprovals_MarksDeferredSignatureFindingsFromTheLedgerOnly()
    {
        var tree = new string('a', 40);
        var source = new FakeApprovals(tree, "lead@example.com");
        var overview = HookRoutes.BuildVcaValidationOverview(new VcaHookValidationSummary(
            HasError: false, HasStopViolation: false, HasCommitViolations: false, RequiredAcknowledgments: [],
            StagedFileCount: 2, ApplicableRuleCount: 4,
            Findings:
            [
                Finding(VcaRuleFindingKind.Deferred, "STOP", rule: "Signed Approval('db.cs', 'lead@example.com, other@example.com')"),
                Finding(VcaRuleFindingKind.Deferred, "STOP", rule: "Signed Approval('docs', 'nobody@example.com')"),
                Finding(VcaRuleFindingKind.Deferred, "STOP", rule: "Human verified"),
                Finding(VcaRuleFindingKind.Deferred, "COMMIT", rule: "Check commit message for words('wip')"),
                Finding(VcaRuleFindingKind.Warning, "WARN", rule: "Signed Approval('db.cs', 'lead@example.com')")
            ]));

        var marked = await HookRoutes.WithStoredApprovalsAsync(overview, _ => Task.FromResult<string?>(tree), source, TestContext.Current.CancellationToken);

        Assert.Equal(["lead@example.com", null, "lead@example.com", null, null], marked.Findings.Select(finding => finding.ApprovedBy));
        Assert.Equal("attention", marked.Outcome); // the warning finding still counts; the annotation changes nothing else
        Assert.Equal(4, marked.DeferredCount);
        Assert.Equal(3, source.Requests.Count);
        Assert.All(source.Requests, request => Assert.Equal(tree, request.TreeId));
        Assert.Equal(["lead@example.com", "other@example.com"], source.Requests[0].Approvers);
        Assert.Equal(["nobody@example.com"], source.Requests[1].Approvers);
        Assert.Empty(source.Requests[2].Approvers);

        // Without a ledger, a staged tree, or any signature finding, the overview is untouched and nothing is asked.
        var quiet = new FakeApprovals(tree, "lead@example.com");
        Assert.Same(overview, await HookRoutes.WithStoredApprovalsAsync(overview, _ => Task.FromResult<string?>(tree), null, TestContext.Current.CancellationToken));
        Assert.Same(overview, await HookRoutes.WithStoredApprovalsAsync(overview, _ => Task.FromResult<string?>(null), quiet, TestContext.Current.CancellationToken));
        Assert.Same(overview, await HookRoutes.WithStoredApprovalsAsync(overview, _ => throw new IOException("git missing"), quiet, TestContext.Current.CancellationToken));
        var plain = HookRoutes.BuildVcaValidationOverview(new VcaHookValidationSummary(
            HasError: false, HasStopViolation: false, HasCommitViolations: false, RequiredAcknowledgments: [],
            StagedFileCount: 1, ApplicableRuleCount: 1, Findings: [Finding(VcaRuleFindingKind.Warning, "WARN")]));
        Assert.Same(plain, await HookRoutes.WithStoredApprovalsAsync(plain, _ => Task.FromResult<string?>(tree), quiet, TestContext.Current.CancellationToken));
        Assert.Empty(quiet.Requests);
    }

    private sealed class FakeApprovals(string tree, string signer) : IStoredCommitApprovalSource
    {
        public List<HumanSignatureRequest> Requests { get; } = [];

        public Task<StoredCommitApproval?> FindAsync(HumanSignatureRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var fits = request.TreeId == tree
                && (request.Approvers.Count == 0 || request.Approvers.Contains(signer, StringComparer.OrdinalIgnoreCase));
            return Task.FromResult(fits
                ? new StoredCommitApproval(new VibeRails.Services.VCA.CommitSignature(Guid.NewGuid(), new string('f', 64), tree, "sig"), signer, Guid.NewGuid().ToString("D"))
                : null);
        }
    }

    private static VcaRuleFinding Finding(
        VcaRuleFindingKind kind,
        string enforcement,
        string? acknowledgment = null,
        string rule = "Example rule") =>
        new(
            kind,
            enforcement,
            rule,
            "Example reason",
            "vc.rules.md",
            "Example guidance",
            acknowledgment);
}
