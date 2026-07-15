using VibeRails.Services.VCA.Hooks;

namespace VibeRails.Services.GitPreflight;

public sealed class VcaPreflightStep : IGitPreflightStep
{
    public const string Id = "vca";
    private readonly IVcaHookValidationService _validationService;

    public VcaPreflightStep(IVcaHookValidationService validationService)
    {
        _validationService = validationService;
    }

    public string StepId => Id;
    public string DisplayName => "VCA rule validation";
    public bool CanBlock => true;

    public async Task<GitPreflightStepResult> ExecuteAsync(
        GitPreflightStepContext context,
        CancellationToken cancellationToken)
    {
        VcaHookValidationResult validation;
        if (context.Request.Invocation.DemoUi)
        {
            await Task.Delay(context.Request.Invocation.DemoDuration, cancellationToken);
            validation = new VcaHookValidationResult(
                "PASS: VCA hook preview completed.",
                new VcaHookValidationSummary(false, false, false, []));
        }
        else
        {
            validation = await _validationService.ValidateAsync(
                context.Request.Invocation,
                context.Snapshot,
                cancellationToken);
        }

        var output = validation.Output
            .TrimEnd()
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .ToList();
        foreach (var line in output)
        {
            await context.WriteOutputAsync(line, null, cancellationToken);
        }

        var status = validation.Summary.HasError
            ? GitPreflightStepStatus.Error
            : validation.Summary.HasStopViolation
                ? GitPreflightStepStatus.Blocked
                : validation.Summary.HasCommitViolations
                    ? GitPreflightStepStatus.Warning
                    : GitPreflightStepStatus.Passed;
        var summary = status switch
        {
            GitPreflightStepStatus.Error => "VCA could not validate the staged snapshot.",
            GitPreflightStepStatus.Blocked => "VCA found STOP-level violations.",
            GitPreflightStepStatus.Warning => "VCA requires commit-message acknowledgment.",
            _ => "VCA rules passed."
        };

        return new GitPreflightStepResult(
            Id,
            DisplayName,
            status,
            summary,
            output,
            DurationMs: 0,
            Blocking: true,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hasError"] = validation.Summary.HasError.ToString(),
                ["hasStopViolation"] = validation.Summary.HasStopViolation.ToString(),
                ["hasCommitViolations"] = validation.Summary.HasCommitViolations.ToString(),
                ["requiredAcknowledgments"] = string.Join("\n", validation.Summary.RequiredAcknowledgments)
            },
            validation.Summary);
    }
}
