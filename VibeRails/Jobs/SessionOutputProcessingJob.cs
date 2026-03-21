using VibeRails.Interfaces;
using VibeRails.Services;
using System.Text;
using VibeRails.DTOs;

namespace VibeRails.Jobs;

public sealed class SessionOutputProcessingJob(
    ILogger<SessionOutputProcessingJob> logger,
    ISystemResourceService resources,
    IServiceScopeFactory scopeFactory,
    ISessionOutputParser sessionOutputParser) : JobBase(logger, resources)
{
    private const int BatchSize = 5;

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);
    protected override JobPriority Priority => JobPriority.Low;

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var dbService = scope.ServiceProvider.GetRequiredService<IDbService>();
        var sessionIds = await dbService.GetEndedUnprocessedSessionIdsAsync(BatchSize, cancellationToken);

        foreach (var sessionId in sessionIds)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var chunks = await dbService.GetSessionLogChunksAsync(sessionId, cancellationToken);
                var userInputs = await dbService.GetUserInputsForSessionAsync(sessionId, cancellationToken);
                var text = await BuildTranscriptAsync(chunks, userInputs, cancellationToken);
                await dbService.SaveSessionOutputAndMarkProcessedAsync(sessionId, text, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "[SessionOutputProcessingJob] Failed to parse session output for {SessionId}",
                    sessionId);
            }
        }
    }

    private async Task<string> BuildTranscriptAsync(
        IReadOnlyList<SessionLogChunkRecord> chunks,
        IReadOnlyList<UserInputRecord> userInputs,
        CancellationToken cancellationToken)
    {
        if (userInputs.Count == 0)
            return await sessionOutputParser.ParseAsync(chunks, cancellationToken);

        var transcript = new StringBuilder();
        var firstInput = userInputs[0];
        var preambleChunks = GetChunksInWindow(chunks, null, firstInput.TimestampUTC);
        var preambleText = await ParseChunksAsync(preambleChunks, cancellationToken);
        AppendAssistantSection(transcript, preambleText, includeWhenEmpty: false);

        for (var i = 0; i < userInputs.Count; i++)
        {
            var currentInput = userInputs[i];
            var nextInputTimestamp = i + 1 < userInputs.Count
                ? userInputs[i + 1].TimestampUTC
                : (DateTime?)null;

            var windowChunks = GetChunksInWindow(chunks, currentInput.TimestampUTC, nextInputTimestamp);
            var assistantText = await ParseChunksAsync(windowChunks, cancellationToken);
            AppendMessageSection(transcript, i + 1, currentInput.InputText, assistantText);
        }

        return transcript.ToString().TrimEnd();
    }

    private async Task<string> ParseChunksAsync(
        IReadOnlyList<SessionLogChunkRecord> chunks,
        CancellationToken cancellationToken)
    {
        if (chunks.Count == 0)
            return string.Empty;

        return await sessionOutputParser.ParseAsync(chunks, cancellationToken);
    }

    private static List<SessionLogChunkRecord> GetChunksInWindow(
        IReadOnlyList<SessionLogChunkRecord> chunks,
        DateTime? startInclusiveUtc,
        DateTime? endExclusiveUtc)
    {
        var window = new List<SessionLogChunkRecord>();

        foreach (var chunk in chunks)
        {
            if (startInclusiveUtc.HasValue && chunk.TimestampUtc < startInclusiveUtc.Value)
                continue;

            if (endExclusiveUtc.HasValue && chunk.TimestampUtc >= endExclusiveUtc.Value)
                continue;

            window.Add(chunk);
        }

        return window;
    }

    private static void AppendAssistantSection(StringBuilder transcript, string assistantText, bool includeWhenEmpty)
    {
        var normalizedAssistantText = NormalizeBlock(assistantText);
        if (!includeWhenEmpty && normalizedAssistantText.Length == 0)
            return;

        AppendBlankLineIfNeeded(transcript);
        transcript.AppendLine("Assistant said:");
        if (normalizedAssistantText.Length > 0)
            transcript.AppendLine(normalizedAssistantText);
    }

    private static void AppendMessageSection(
        StringBuilder transcript,
        int messageNumber,
        string userText,
        string assistantText)
    {
        AppendBlankLineIfNeeded(transcript);
        transcript.AppendLine($">>>>>>>>> Message {messageNumber}");
        transcript.AppendLine("User said:");
        transcript.AppendLine(NormalizeBlock(userText));
        transcript.AppendLine($"<<<<<<<<< End Message {messageNumber}");
        transcript.AppendLine("Assistant said:");

        var normalizedAssistantText = NormalizeBlock(assistantText);
        if (normalizedAssistantText.Length > 0)
            transcript.AppendLine(normalizedAssistantText);
    }

    private static void AppendBlankLineIfNeeded(StringBuilder transcript)
    {
        if (transcript.Length == 0)
            return;

        if (!transcript.ToString().EndsWith(Environment.NewLine + Environment.NewLine, StringComparison.Ordinal))
            transcript.AppendLine();
    }

    private static string NormalizeBlock(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        return text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }
}
