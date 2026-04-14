namespace VibeRails.Services.UserInOut;

/// <summary>
/// Read façade for cleaned user input text. Consumers call this to get
/// what the user typed, cleaned of ANSI, secrets, and noise.
/// Returns "" for inputs that haven't been cleaned yet or were filtered out.
/// Never triggers synchronous cleaning — that's the write pipeline's job.
/// </summary>
public interface IUserTextOutput
{
    /// <summary>
    /// Concatenated cleaned text for every cleaned input in the session,
    /// ordered by UserInputs.Sequence, separated by newlines.
    /// Uncleaned and filtered-out inputs are silently skipped.
    /// Returns "" if nothing is cleaned yet or the session has no inputs.
    /// </summary>
    Task<string> GetSessionTextAsync(string sessionId, CancellationToken ct = default);

    /// <summary>
    /// Cleaned text for one input id.
    /// Returns "" if the row doesn't exist, hasn't been cleaned yet, or was filtered out.
    /// </summary>
    Task<string> GetTextForInputIdAsync(long userInputId, CancellationToken ct = default);
}
