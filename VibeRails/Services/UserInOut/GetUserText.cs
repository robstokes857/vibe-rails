using VibeRails.DB;

namespace VibeRails.Services.UserInOut;

public class GetUserText : IGetUserText
{
    private readonly IRepository _repository;

    public GetUserText(IRepository repository)
    {
        _repository = repository;
    }

    public Task<string> GetTextForInputIdAsync(long userInputId, int? maxChars = null, CancellationToken ct = default)
        => _repository.GetTextForInputIdOrRawAsync(userInputId, maxChars, ct);

    public Task<string> GetFirstInputTextForSessionAsync(string sessionId, int? maxChars = null, CancellationToken ct = default)
        => _repository.GetFirstInputTextForSessionOrRawAsync(sessionId, maxChars, ct);
}
