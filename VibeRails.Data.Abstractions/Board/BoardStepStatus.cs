namespace VibeRails.Services.Board;

/// <summary>Stable persisted and wire values for lane Automation progress. Do not rename existing values.</summary>
public static class BoardStepStatus
{
    /// <summary>Waiting lane Automation status.</summary>
    public const string Waiting = "Waiting";
    /// <summary>Queued lane Automation status.</summary>
    public const string Queued = "Queued";
    /// <summary>Running lane Automation status.</summary>
    public const string Running = "Running";
    /// <summary>Succeeded lane Automation status.</summary>
    public const string Succeeded = "Succeeded";
    /// <summary>Failed lane Automation status.</summary>
    public const string Failed = "Failed";
    /// <summary>Cancelled lane Automation status.</summary>
    public const string Cancelled = "Cancelled";
    /// <summary>Interrupted lane Automation status.</summary>
    public const string Interrupted = "Interrupted";
    /// <summary>Timed out lane Automation status.</summary>
    public const string TimedOut = "TimedOut";
    /// <summary>Skipped lane Automation status.</summary>
    public const string Skipped = "Skipped";
    /// <summary>Unknown lane Automation status.</summary>
    public const string Unknown = "Unknown";
    /// <summary>Awaiting result lane Automation status.</summary>
    public const string AwaitingResult = "Awaiting result";
    /// <summary>Reviewing lane Automation status.</summary>
    public const string Reviewing = "Reviewing";
    /// <summary>Fixing lane Automation status.</summary>
    public const string Fixing = "Fixing";
    /// <summary>Stopping lane Automation status.</summary>
    public const string Stopping = "Stopping";
    /// <summary>Passed lane Automation status.</summary>
    public const string Passed = "Passed";
}
