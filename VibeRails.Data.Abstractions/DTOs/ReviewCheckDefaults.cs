namespace VibeRails.DTOs;

/// <summary>Editable starter review actions. Template creation inserts these before its single Worker.</summary>
public static class ReviewCheckDefaults
{
    /// <summary>
    /// Uses unpushed committed changes so committing does not turn a review into an empty-tree pass.
    /// Missing upstream is a visible execution failure; the reviewer still receives that evidence.
    /// </summary>
    public static List<JobActionRequest> Create() =>
    [
        new(null, JobActionKind.CodeQuality, Arguments: ["unpushed"]),
        new(null, JobActionKind.Vca, Arguments: ["unpushed"])
    ];
}
