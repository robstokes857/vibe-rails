using VibeRails.DTOs;
using VibeRails.Interfaces;
using System.Text;

namespace VibeRails.Services;

public class SessionTranscriptService(
    IDbService dbService,
    ISessionOutputParser sessionOutputParser) : ISessionTranscriptService
{
    public async Task<string> GetOrBuildAsync(string sessionId, CancellationToken cancellationToken)
    {
        var existing = await dbService.GetSessionOutputAsync(sessionId, cancellationToken);
        if (existing != null && !string.IsNullOrWhiteSpace(existing.Text))
            return existing.Text;

        var chunks = await dbService.GetSessionLogChunksAsync(sessionId, cancellationToken);
        var userInputs = await dbService.GetUserInputsForSessionAsync(sessionId, cancellationToken);
        var text = await BuildTranscriptAsync(chunks, userInputs, cancellationToken);

        await dbService.SaveSessionOutputAndMarkProcessedAsync(sessionId, text, cancellationToken);

        return text;
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
