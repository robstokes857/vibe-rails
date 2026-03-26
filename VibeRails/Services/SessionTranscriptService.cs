using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using System.Text;

namespace VibeRails.Services;

public class SessionTranscriptService(
    IRepository repository,
    ISessionOutputParser sessionOutputParser) : ISessionTranscriptService
{
    public async Task<string> GetOrBuildAsync(string sessionId, CancellationToken cancellationToken, bool forceRebuild = false)
    {
        var existing = await repository.GetSessionOutputAsync(sessionId, cancellationToken);
        if (!forceRebuild && existing != null && !string.IsNullOrWhiteSpace(existing.Text))
            return existing.Text;

        var chunks = await repository.GetSessionLogChunksAsync(sessionId, cancellationToken);
        var userInputs = await repository.GetUserInputsForSessionAsync(sessionId, cancellationToken);
        var text = await BuildTranscriptAsync(chunks, userInputs, cancellationToken);

        await repository.SaveSessionOutputAndMarkProcessedAsync(sessionId, text, cancellationToken);

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

        for (var i = 0; i < userInputs.Count; i++)
        {
            var currentInput = userInputs[i];
            var nextInputTimestamp = i + 1 < userInputs.Count
                ? userInputs[i + 1].TimestampUTC
                : (DateTime?)null;

            var windowChunks = GetChunksInWindow(chunks, currentInput.TimestampUTC, nextInputTimestamp);
            var assistantText = await ParseChunksAsync(windowChunks, cancellationToken);
            AppendMessageSection(transcript, currentInput.InputText, assistantText);
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

    private static void AppendMessageSection(
        StringBuilder transcript,
        string userText,
        string assistantText)
    {
        AppendBlankLineIfNeeded(transcript);
        transcript.AppendLine("User:");
        transcript.AppendLine(NormalizeBlock(userText));
        transcript.AppendLine();
        transcript.AppendLine("Assistant:");

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
