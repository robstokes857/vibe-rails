using System.Text.RegularExpressions;

namespace VibeRails.Services.VCA.Hooks;

public interface IVcaHookValidationAnalyzer
{
    VcaHookValidationSummary Analyze(string validationOutput);
    bool ShouldBlockPreCommit(VcaHookValidationSummary summary);
    List<string> GetMissingAcknowledgments(string commitMessage, IReadOnlyList<string> requiredAcknowledgments);
}

public sealed class VcaHookValidationAnalyzer : IVcaHookValidationAnalyzer
{
    private static readonly Regex AcknowledgmentPattern = new(
        @"\[VCA:[^\]\r\n]+\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public VcaHookValidationSummary Analyze(string validationOutput)
    {
        var requiredAcknowledgments = AcknowledgmentPattern
            .Matches(validationOutput)
            .Select(match => match.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Anchor to the emitted verdict line, or to a "[STOP]" that begins a violation
        // line — NOT any "[STOP]" substring, so a non-blocking WARN/COMMIT rule whose text
        // merely mentions "[STOP]" cannot flip this to a hard block.
        var hasStopViolation =
            validationOutput.Contains("STOP-level violations", StringComparison.OrdinalIgnoreCase) ||
            HasLineStartingWith(validationOutput, "[STOP]");

        var hasError = validationOutput.TrimStart().StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase);

        return new VcaHookValidationSummary(
            HasError: hasError,
            HasStopViolation: hasStopViolation,
            HasCommitViolations: requiredAcknowledgments.Count > 0,
            RequiredAcknowledgments: requiredAcknowledgments);
    }

    public bool ShouldBlockPreCommit(VcaHookValidationSummary summary) =>
        summary.HasError || summary.HasStopViolation;

    private static bool HasLineStartingWith(string text, string prefix)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public List<string> GetMissingAcknowledgments(
        string commitMessage,
        IReadOnlyList<string> requiredAcknowledgments)
    {
        return requiredAcknowledgments
            .Where(token => !commitMessage.Contains(token, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}
