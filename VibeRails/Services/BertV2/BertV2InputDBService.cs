using Serilog;
using VibeRails.Services.UserInOut;

namespace VibeRails.Services.BertV2;

public class BertV2InputDBService : IBertV2InputDBService
{
    private readonly IBertV2InputService _inputService;
    private readonly IGetUserText _userText;

    public BertV2InputDBService(
        IBertV2InputService inputService,
        IGetUserText userText)
    {
        _inputService = inputService;
        _userText = userText;
    }

    public async Task CaptureUserInputAsync(string sessionId, long userInputId, CancellationToken cancellationToken = default)
    {
        var text = await _userText.GetTextForInputIdAsync(userInputId, ct: cancellationToken);
        if (string.IsNullOrEmpty(text))
        {
            Log.Debug("[BERT-ETL] Skipped input {UserInputId}: row missing", userInputId);
            return;
        }

        _inputService.Capture(sessionId, userInputId, text);
    }
}
