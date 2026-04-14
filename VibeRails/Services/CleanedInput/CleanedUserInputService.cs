using VibeRails.DB;
using VibeRails.Services.UserInOut;
using System.Text;

namespace VibeRails.Services.CleanedInput;

/// <summary>
/// Write primitive for cleaning user input. Runs <see cref="InputEtlFilter"/>
/// and persists the result with dual-FK linking.
/// </summary>
public sealed class CleanedUserInputService : ICleanedUserInputService
{
    private readonly IRepository _repository;
    private readonly ITuiTextExtractor _tuiTextExtractor;
    private readonly ILogger<CleanedUserInputService>? _logger;

    public CleanedUserInputService(
        IRepository repository,
        ITuiTextExtractor tuiTextExtractor,
        ILogger<CleanedUserInputService>? logger = null)
    {
        _repository = repository;
        _tuiTextExtractor = tuiTextExtractor;
        _logger = logger;
    }

    public string? CleanText(string rawInput, string? sessionTuiOutput = null)
    {
        var extracted = string.IsNullOrWhiteSpace(sessionTuiOutput)
            ? rawInput
            : _tuiTextExtractor.Extract(rawInput, sessionTuiOutput);

        return InputEtlFilter.Process(extracted);
    }

    public async Task CleanAndPersistAsync(
        long userInputId,
        string? sessionTuiOutput = null,
        CancellationToken cancellationToken = default)
    {
        // Idempotent: skip if already cleaned
        if (await _repository.IsInputCleanedAsync(userInputId, cancellationToken))
            return;

        var raw = await _repository.GetUserInputByIdAsync(userInputId, cancellationToken);
        if (raw is null)
        {
            _logger?.LogWarning("[CleanedInput] UserInput {Id} not found, skipping", userInputId);
            return;
        }

        if (string.IsNullOrWhiteSpace(sessionTuiOutput))
        {
            sessionTuiOutput = await TryLoadSessionTuiOutputAsync(raw.SessionId, cancellationToken);
        }

        var cleaned = CleanText(raw.InputText, sessionTuiOutput);

        // Persist: filtered-out inputs get CleanedText = "" so they're marked as processed.
        await _repository.CreateCleanedAndLinkAsync(
            raw.SessionId,
            userInputId,
            cleaned ?? "",
            DateTime.UtcNow,
            cancellationToken);
    }

    private async Task<string?> TryLoadSessionTuiOutputAsync(string sessionId, CancellationToken cancellationToken)
    {
        try
        {
            var chunks = await _repository.GetSessionLogChunksAsync(sessionId, cancellationToken);
            if (chunks.Count == 0)
                return null;

            var totalLength = chunks.Sum(static chunk => chunk.Content.Length);
            if (totalLength == 0)
                return null;

            var combined = new byte[totalLength];
            var offset = 0;
            foreach (var chunk in chunks)
            {
                Buffer.BlockCopy(chunk.Content, 0, combined, offset, chunk.Content.Length);
                offset += chunk.Content.Length;
            }

            return Encoding.UTF8.GetString(combined);
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(
                ex,
                "[CleanedInput] Failed to load TUI output for session {SessionId}; falling back to raw input",
                sessionId);
            return null;
        }
    }
}
