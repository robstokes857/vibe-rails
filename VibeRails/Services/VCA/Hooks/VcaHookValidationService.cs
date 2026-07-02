using VibeRails.Services.Mcp.Tools;

namespace VibeRails.Services.VCA.Hooks;

public interface IVcaHookValidationService
{
    Task<VcaHookValidationResult> ValidateAsync(string workingDirectory, CancellationToken cancellationToken);
}

public sealed class VcaRulesHookValidationService : IVcaHookValidationService
{
    public async Task<VcaHookValidationResult> ValidateAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
