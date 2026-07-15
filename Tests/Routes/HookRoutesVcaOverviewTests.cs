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
            RequiredAcknowledgments: ["[VCA:AGENTS.md:package-changes]"],
            StagedFileCount: 3,
            ApplicableRuleCount: 4,
            Findings:
            [
                Finding(VcaRuleFindingKind.Blocked, "STOP"),
                Finding(VcaRuleFindingKind.AcknowledgmentRequired, "COMMIT", "[VCA:AGENTS.md:package-changes]"),
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

    private static VcaRuleFinding Finding(
        VcaRuleFindingKind kind,
        string enforcement,
        string? acknowledgment = null) =>
        new(
            kind,
            enforcement,
            "Example rule",
            "Example reason",
            "AGENTS.md",
            "Example guidance",
            acknowledgment);
}
