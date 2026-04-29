namespace VibeRails.Services.UserInOut;

public interface IGetUserText
{
    Task<string> GetTextForInputIdAsync(long userInputId, int? maxChars = null, CancellationToken ct = default);

    Task<string> GetFirstInputTextForSessionAsync(string sessionId, int? maxChars = null, CancellationToken ct = default);
}
