namespace VibeRails.Services.BertV2;

public interface IBertV2InputDBService
{
    Task CaptureUserInputAsync(string sessionId, long userInputId, CancellationToken cancellationToken = default);
}
