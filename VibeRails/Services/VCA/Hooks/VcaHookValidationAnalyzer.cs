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
            .Where(token => !HasAcknowledgmentWithReason(commitMessage, token))
            .ToList();
    }

    private static bool HasAcknowledgmentWithReason(string commitMessage, string token)
    {
        foreach (var rawLine in commitMessage.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var tokenIndex = line.IndexOf(token, StringComparison.OrdinalIgnoreCase);
            if (tokenIndex < 0)
            {
                continue;
            }

            var remainder = line[(tokenIndex + token.Length)..];
            var reasonIndex = remainder.IndexOf("Reason:", StringComparison.OrdinalIgnoreCase);
            if (reasonIndex < 0)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(remainder[(reasonIndex + "Reason:".Length)..]))
            {
                return true;
            }
        }

        return false;
    }
}
