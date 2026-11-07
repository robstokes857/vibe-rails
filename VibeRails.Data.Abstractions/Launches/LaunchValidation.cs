using System.Text.RegularExpressions;

namespace VibeRails.Launches;

/// <summary>Input rules shared by enqueue, retry and the process that owns execution.</summary>
public static partial class LaunchValidation
{
    public const string RetiredStepMessage = "{{step:...}} is retired. Remove the step reference; use a separate script Action when needed.";
    public static void RejectRetiredPrompt(string? prompt)
    {
        if (prompt is not null && RetiredStep().IsMatch(prompt)) throw new LaunchValidationException(RetiredStepMessage);
    }
    public static void ValidateSnapshot(LaunchSnapshot snapshot)
    {
        if (snapshot.Context is not { } context || string.IsNullOrWhiteSpace(context.ProjectPath)
            || string.IsNullOrWhiteSpace(context.WorkingDirectory)
            || string.IsNullOrWhiteSpace(context.OccurrenceId) || context.OccurrenceId.Length > 1_000
            || context.OccurrenceId.Any(char.IsControl)
            || context.TriggerKind is not ("manual" or "board" or "board-lane" or "schedule" or "before-commit" or "after-commit" or "binding"))
            throw new LaunchValidationException("The run requires a valid project, occurrence and supported trigger context.");
        if ((snapshot.Preset is null) == (snapshot.Action is null)) throw new LaunchValidationException("A run must contain exactly one preset or Action.");
        RejectRetiredPrompt(snapshot.Preset?.InitialPrompt);
        RejectRetiredPrompt(snapshot.Context.PromptPrefix);
        RejectRetiredPrompt(snapshot.Context.PromptOverride);
        RejectRetiredPrompt(snapshot.Context.ComposedPrompt);
        if (snapshot.Preset is { Kind: "quick-launch" } && snapshot.Context.TriggerKind != "manual")
            throw new LaunchValidationException("Quick Launches are available only for manual terminal launches.");
    }
    [GeneratedRegex(@"\{\{\s*step\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RetiredStep();
}
