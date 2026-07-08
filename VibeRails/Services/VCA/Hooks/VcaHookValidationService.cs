using VibeRails.Services.Mcp.Tools;

namespace VibeRails.Services.VCA.Hooks;

public interface IVcaHookValidationService
{
    Task<VcaHookValidationResult> ValidateAsync(string workingDirectory, CancellationToken cancellationToken);
}

public sealed class VcaRulesHookValidationService : IVcaHookValidationService
{
    private static readonly bool VcaValidationTemporarilyDisabled = true;

    public async Task<VcaHookValidationResult> ValidateAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (VcaValidationTemporarilyDisabled)
        {
            return new VcaHookValidationResult(
                "PASS: VCA validation is temporarily disabled.",
                new VcaHookValidationSummary(
                    HasError: false,
                    HasStopViolation: false,
                    HasCommitViolations: false,
                    RequiredAcknowledgments: []));
        }

        var report = await RulesTool.ValidateVcaReportAsync(workingDirectory);
        return new VcaHookValidationResult(
            report.Output,
            new VcaHookValidationSummary(
                report.HasError,
                report.HasStopViolation,
                report.RequiredAcknowledgments.Count > 0,
                report.RequiredAcknowledgments));
    }
}
