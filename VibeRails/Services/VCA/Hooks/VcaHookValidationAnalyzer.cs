namespace VibeRails.Services.VCA.Hooks;

public interface IVcaHookValidationAnalyzer
{
    bool ShouldBlockPreCommit(VcaHookValidationSummary summary);
    List<string> GetMissingAcknowledgments(string commitMessage, IReadOnlyList<string> requiredAcknowledgments);
}

public sealed class VcaHookValidationAnalyzer : IVcaHookValidationAnalyzer
{
    public bool ShouldBlockPreCommit(VcaHookValidationSummary summary) =>
        summary.HasError || summary.HasStopViolation;

    public List<string> GetMissingAcknowledgments(
        string commitMessage,
        IReadOnlyList<string> requiredAcknowledgments)
    {
        return requiredAcknowledgments
            .Where(token => !commitMessage.Contains(token, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
