namespace VibeRails.DTOs;

/// <summary>Optional Worker classification. Only Code review changes the review workflow.</summary>
public static class AgentPurpose
{
    public const string Unspecified = "work";

    /// <summary>The persisted legacy work value is the optional, unspecified choice.</summary>
    public static bool IsValid(string? value) => value is "work" or "code_review" or "testing" or "building" or "deploying" or "documentation" or "other";
}
