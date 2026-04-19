namespace VibeRails.Services.BertV2;

public interface IBertV2InputDBService
{
    /// <summary>
    /// Capture a user input into the BERT vector store. Reads cleaned text via
    /// <see cref="UserInOut.IGetCleanedUserText"/>. If the input hasn't been cleaned yet
    /// (returns ""), the call silently no-ops.
    /// </summary>
    Task CaptureUserInputAsync(string sessionId, long userInputId, CancellationToken cancellationToken = default);
}
