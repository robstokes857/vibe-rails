namespace VibeRails.Services.Bert;

public interface IBertInputCaptureService
{
    Task CaptureAsync(
        string sessionId,
        long userInputId,
        string inputText,
        CancellationToken cancellationToken = default);
}
