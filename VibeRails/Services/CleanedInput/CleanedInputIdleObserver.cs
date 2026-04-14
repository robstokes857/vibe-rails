using VibeRails.DB;
using VibeRails.Services.Terminal;
using System.Text;
using VibeRails.DTOs;

namespace VibeRails.Services.CleanedInput;

/// <summary>
/// Fires on <see cref="TerminalIdleEvent"/> (5s idle) and cleans any uncleaned
/// <c>UserInputs</c> rows for that session. This is the primary cleaning path
/// for active sessions.
/// </summary>
public class CleanedInputIdleObserver : ITerminalIoObserver
{
    private readonly ICleanedUserInputService _cleanedService;
    private readonly IRepository _repository;
    private readonly ILogger<CleanedInputIdleObserver>? _logger;

    public CleanedInputIdleObserver(
        ICleanedUserInputService cleanedService,
        IRepository repository,
        ILogger<CleanedInputIdleObserver>? logger = null)
    {
        _cleanedService = cleanedService;
        _repository = repository;
        _logger = logger;
    }

    public ValueTask OnTerminalIoAsync(TerminalIoEvent ioEvent, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public async ValueTask OnTerminalIdleAsync(TerminalIdleEvent idleEvent, CancellationToken cancellationToken = default)
    {
        var uncleaned = await _repository.GetUncleanedInputsForSessionAsync(idleEvent.SessionId, cancellationToken);
        if (uncleaned.Count == 0)
            return;

        var rawSession = await _repository.GetSessionLogChunksAsync(idleEvent.SessionId, cancellationToken);

        _logger?.LogDebug(
            "[CleanedInputIdle] Cleaning {Count} input(s) for session {SessionId}",
            uncleaned.Count, idleEvent.SessionId);

        for (int i = 0; i < uncleaned.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = uncleaned[i];

            var startTime = i == 0
                ? DateTime.MinValue
                : uncleaned[i - 1].TimestampUTC;
            var endTime = i < uncleaned.Count - 1
                ? uncleaned[i + 1].TimestampUTC
                : DateTime.MaxValue;

            var windowBytes = rawSession
                .Where(c => c.TimestampUtc >= startTime && c.TimestampUtc <= endTime)
                .SelectMany(c => c.Content)
                .ToArray();

            if (windowBytes.Length == 0)
                continue;

            var windowText = Encoding.UTF8.GetString(windowBytes);

            // Fast path: if the raw output contains the input verbatim, it's already clean
            if (windowText.Contains(input.InputText, StringComparison.Ordinal))
            {
                // TODO: persist as-is — input text is the cleaned text
                continue;
            }

            // Slow path: needs actual cleaning with windowed context
            try
            {
                await _cleanedService.CleanAndPersistAsync(input.Id, windowText, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "[CleanedInputIdle] Failed to clean UserInput {UserInputId}",
                    input.Id);
            }
        }
    }
}