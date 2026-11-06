namespace VibeRails.DTOs;

/// <summary>Editable starter review actions. Template creation inserts these before its single Worker.</summary>
public static class ReviewCheckDefaults
{
    /// <summary>
    /// Code quality follows the card's linked commits; VCA retains its explicit unpushed scope.
    /// </summary>
    public static List<JobActionRequest> Create() =>
    [
        new(null, JobActionKind.CodeQuality, Arguments: ["card"]),
        new(null, JobActionKind.Vca, Arguments: ["unpushed"])
    ];
}
