namespace VibeRails.Services.UserInOut;

/// <summary>
/// Best-effort read of what the user typed. Returns cleaned text from
/// CleanedUserInput when available, otherwise the raw UserInputs.InputText,
/// via a SQL-level COALESCE. For UI previews, display names, and anywhere
/// an empty string is worse than a dirty one.
///
/// If the text is going to an LLM, an embedder, or a file the user
/// downloads, use <see cref="IGetCleanedUserText"/> instead — this
/// interface may return raw (potentially secret-bearing) bytes.
/// </summary>
public interface IGetUserText
{
    /// <summary>
    /// Cleaned-or-raw text for one input id. "" only if the row doesn't exist.
    /// </summary>
    Task<string> GetTextForInputIdAsync(long userInputId, int? maxChars = null, CancellationToken ct = default);

    /// <summary>
    /// Cleaned-or-raw text of the session's first input (by Sequence).
    /// "" if the session has no inputs.
    /// </summary>
    Task<string> GetFirstInputTextForSessionAsync(string sessionId, int? maxChars = null, CancellationToken ct = default);
}
