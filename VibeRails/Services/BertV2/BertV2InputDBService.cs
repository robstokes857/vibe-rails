using Serilog;
using VibeRails.Services.UserInOut;

namespace VibeRails.Services.BertV2;

/// <summary>
/// Entry point for capturing user input into the BERT vector store.
/// Reads cleaned text via <see cref="IUserTextOutput"/> — no inline cleaning.
/// If the input hasn't been cleaned yet, the call silently no-ops.
/// </summary>
public class BertV2InputDBService : IBertV2InputDBService
{
    private readonly IBertV2InputService _inputService;
    private readonly IUserTextOutput _userTextOutput;

    public BertV2InputDBService(
        IBertV2InputService inputService,
        IUserTextOutput userTextOutput)
    {
        _inputService = inputService;
        _userTextOutput = userTextOutput;
    }

    public async Task CaptureUserInputAsync(string sessionId, long userInputId, CancellationToken cancellationToken = default)
    {
        var cleanedText = await _userTextOutput.GetTextForInputIdAsync(userInputId, cancellationToken);
        if (string.IsNullOrEmpty(cleanedText))
        {
            Log.Debug("[BERT-ETL] Skipped input {UserInputId}: not yet cleaned or filtered out", userInputId);
            return;
        }

        _inputService.Capture(sessionId, userInputId, cleanedText);
    }
}
