using System.Text.RegularExpressions;

namespace VibeRails.Launches;

/// <summary>Input rules shared by enqueue, retry and the process that owns execution.</summary>
public static partial class LaunchValidation
{
    /// <summary>Maximum UTF-16 length of an optional, per-run user message.</summary>
    public const int MaxUserMessageChars = 8_000;
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
            || context.TriggerKind is not ("manual" or "board" or "board-lane" or "schedule" or "before-commit" or "after-commit" or "binding" or "rule-violation"))
            throw new LaunchValidationException("The run requires a valid project, occurrence and supported trigger context.");
        if ((snapshot.Preset is null) == (snapshot.Action is null)) throw new LaunchValidationException("A run must contain exactly one preset or Action.");
        if (context.UserMessage?.Length > MaxUserMessageChars)
            throw new LaunchValidationException($"User message must be {MaxUserMessageChars:N0} characters or fewer.");
        if (!string.IsNullOrEmpty(context.UserMessage) && snapshot.Preset is null)
            throw new LaunchValidationException("User messages are supported only for Agents and Quick Launches.");
        if (context.TriggerKind == "rule-violation" && context.RuleViolation is null)
            throw new LaunchValidationException("A rule response requires captured violation context.");
        if (context.RuleViolation is not null && snapshot.Action is { Kind: not "script" })
            throw new LaunchValidationException("A rule response must use an AI Agent or script Action.");
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
