using VibeRails.DB;

namespace VibeRails.Services.UserInOut;

public class UserTextOutput : IUserTextOutput
{
    private readonly IRepository _repository;

    public UserTextOutput(IRepository repository)
    {
        _repository = repository;
    }

    public async Task<string> GetSessionTextAsync(string sessionId, CancellationToken ct = default)
    {
        var texts = await _repository.GetSessionCleanedTextOrderedAsync(sessionId, ct);
        if (texts.Count == 0)
            return "";

        // Skip empty strings (sentinel rows for filtered-out / legacy inputs)
        var parts = texts.Where(t => !string.IsNullOrEmpty(t));
        return string.Join("\n", parts);
    }

    public async Task<string> GetTextForInputIdAsync(long userInputId, CancellationToken ct = default)
    {
        var text = await _repository.GetCleanedTextForInputIdAsync(userInputId, ct);
        return text ?? "";
    }
}
