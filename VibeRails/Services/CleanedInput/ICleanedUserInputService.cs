namespace VibeRails.Services.CleanedInput;

/// <summary>
/// Write primitive for cleaning user input text. Runs the ETL pipeline
/// (normalize, filter secrets/noise) and persists the result.
/// Only called by the idle observer and the closed-session background job.
/// </summary>
public interface ICleanedUserInputService
{
    /// <summary>
    /// Pure text-cleaning pipeline. When TUI output is available, first tries to
    /// extract the richer user-visible text from the terminal transcript, then
    /// runs ETL filtering. Returns cleaned text, or null if the input should be
    /// filtered out entirely (secret/noise/empty).
    /// </summary>
    string? CleanText(string rawInput, string? sessionTuiOutput = null);

    /// <summary>
    /// Cleans a single raw UserInput row and persists the result atomically
    /// (inserts CleanedUserInput + updates UserInputs.CleanedId in one transaction).
    /// Idempotent — no-op if the input is already cleaned.
    /// Filtered-out inputs get a CleanedUserInput row with CleanedText = "".
    /// </summary>
    Task CleanAndPersistAsync(
        long userInputId,
        string? sessionTuiOutput = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-runs cleaning for an already-linked input and overwrites the existing
    /// cleaned row. Used to repair historical rows that were populated before a
    /// normalization fix landed.
    /// </summary>
    Task RepairAndPersistAsync(
        long userInputId,
        string? sessionTuiOutput = null,
        CancellationToken cancellationToken = default);
}
