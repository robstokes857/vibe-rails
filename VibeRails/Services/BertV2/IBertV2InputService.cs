namespace VibeRails.Services.BertV2;

public interface IBertV2InputService
{
    void Capture(string sessionId, long userInputId, string inputText);
}
