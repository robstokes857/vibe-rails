using VibeRails.DTOs;
using VibeRails.Services.UserInOut;

namespace VibeRails.Services.BertV2;

public sealed class BertDocumentResponseMapper : IBertDocumentResponseMapper
{
    // Defense in depth. New captures already pass through InputEtlFilter, and
    // pre-filter vector docs are purged by BertV2VectorStore.MaybePurgeLegacy…,
    // but the metadata path still reads raw UserInputs.InputText. We re-check
    // every displayed string here so a stray secret can never leave the API.
    internal const string RedactionMarker = "[redacted — text matches secret pattern]";

    private static string RedactIfSecret(string text)
        => InputEtlFilter.ContainsSecret(text) ? RedactionMarker : text;

    public BertCaptureSummaryResponse ToCaptureSummary(BertStoredDocument document, BertInputMetadata? metadata)
    {
        var parsedId = BertDocumentId.Parse(document.DocumentId);
        var preview = BuildPreview(metadata?.InputText, document.RawText);

        return new BertCaptureSummaryResponse(
            document.DocumentId,
            metadata?.SessionId ?? parsedId.SessionId ?? string.Empty,
            metadata?.UserInputId ?? parsedId.UserInputId,
            metadata?.Sequence,
            metadata?.TimestampUtc,
            metadata?.Cli,
            metadata?.EnvironmentName,
            metadata?.WorkingDirectory,
            metadata?.GitCommitHash ?? ExtractGitCommit(document.RawText),
            RedactIfSecret(preview),
            metadata?.FileChangeCount ?? 0,
            Kind: "input",
            ChunkIndex: null);
    }

    public BertCaptureDetailResponse ToCaptureDetail(BertStoredDocument document, BertInputMetadata? metadata, IReadOnlyList<BertFileChangeResponse> fileChanges)
    {
        var parsedId = BertDocumentId.Parse(document.DocumentId);
        var userText = metadata?.InputText ?? ExtractUserText(document.RawText);
        var rawText = NormalizeNewlines(document.RawText);

        return new BertCaptureDetailResponse(
            document.DocumentId,
            metadata?.SessionId ?? parsedId.SessionId ?? string.Empty,
            metadata?.UserInputId ?? parsedId.UserInputId,
            metadata?.Sequence,
            metadata?.TimestampUtc,
            metadata?.Cli,
            metadata?.EnvironmentName,
            metadata?.WorkingDirectory,
            metadata?.GitCommitHash ?? ExtractGitCommit(document.RawText),
            RedactIfSecret(userText),
            fileChanges.ToList(),
            RedactIfSecret(rawText),
            Kind: "input",
            ChunkIndex: null);
    }

    public BertSearchHitResponse ToSearchHit(BertStoredDocument document, BertInputMetadata? metadata)
    {
        var parsedId = BertDocumentId.Parse(document.DocumentId);
        var preview = BuildPreview(metadata?.InputText, document.RawText);

        return new BertSearchHitResponse(
            document.DocumentId,
            metadata?.SessionId ?? parsedId.SessionId ?? string.Empty,
            metadata?.UserInputId ?? parsedId.UserInputId,
            metadata?.Sequence,
            metadata?.TimestampUtc,
            metadata?.Cli,
            metadata?.EnvironmentName,
            metadata?.WorkingDirectory,
            metadata?.GitCommitHash ?? ExtractGitCommit(document.RawText),
            RedactIfSecret(preview),
            metadata?.FileChangeCount ?? 0,
            document.Score,
            Kind: "input",
            ChunkIndex: null);
    }

    public BertCaptureSummaryResponse ToSessionCaptureSummary(BertStoredDocument document, BertSessionMetadata? metadata)
    {
        var parsedId = BertSessionDocumentId.Parse(document.DocumentId);
        var sessionId = metadata?.SessionId ?? parsedId?.SessionId ?? string.Empty;
        var chunkIndex = metadata?.ChunkIndex ?? parsedId?.ChunkIndex;
        var preview = BuildPreview(document.RawText, document.RawText);

        return new BertCaptureSummaryResponse(
            document.DocumentId,
            sessionId,
            UserInputId: null,
            Sequence: null,
            metadata?.EndedUtc ?? metadata?.StartedUtc,
            metadata?.Cli,
            metadata?.EnvironmentName,
            metadata?.WorkingDirectory,
            GitCommitHash: null,
            RedactIfSecret(preview),
            metadata?.FileChanges.Count ?? 0,
            Kind: "session-chunk",
            ChunkIndex: chunkIndex);
    }

    public BertCaptureDetailResponse ToSessionCaptureDetail(BertStoredDocument document, BertSessionMetadata? metadata)
    {
        var parsedId = BertSessionDocumentId.Parse(document.DocumentId);
        var sessionId = metadata?.SessionId ?? parsedId?.SessionId ?? string.Empty;
        var chunkIndex = metadata?.ChunkIndex ?? parsedId?.ChunkIndex;
        var chunkText = RedactIfSecret(NormalizeNewlines(document.RawText));
        var fileChanges = metadata?.FileChanges.ToList() ?? [];

        return new BertCaptureDetailResponse(
            document.DocumentId,
            sessionId,
            UserInputId: null,
            Sequence: null,
            metadata?.EndedUtc ?? metadata?.StartedUtc,
            metadata?.Cli,
            metadata?.EnvironmentName,
            metadata?.WorkingDirectory,
            GitCommitHash: null,
            chunkText,
            fileChanges,
            chunkText,
            Kind: "session-chunk",
            ChunkIndex: chunkIndex);
    }

    public BertSearchHitResponse ToSessionSearchHit(BertStoredDocument document, BertSessionMetadata? metadata)
    {
        var parsedId = BertSessionDocumentId.Parse(document.DocumentId);
        var sessionId = metadata?.SessionId ?? parsedId?.SessionId ?? string.Empty;
        var chunkIndex = metadata?.ChunkIndex ?? parsedId?.ChunkIndex;
        var preview = BuildPreview(document.RawText, document.RawText);

        return new BertSearchHitResponse(
            document.DocumentId,
            sessionId,
            UserInputId: null,
            Sequence: null,
            metadata?.EndedUtc ?? metadata?.StartedUtc,
            metadata?.Cli,
            metadata?.EnvironmentName,
            metadata?.WorkingDirectory,
            GitCommitHash: null,
            RedactIfSecret(preview),
            metadata?.FileChanges.Count ?? 0,
            document.Score,
            Kind: "session-chunk",
            ChunkIndex: chunkIndex);
    }

    private static string BuildPreview(string? inputText, string rawText)
    {
        var text = !string.IsNullOrWhiteSpace(inputText)
            ? NormalizeWhitespace(inputText)
            : NormalizeWhitespace(ExtractUserText(rawText));

        if (string.IsNullOrWhiteSpace(text))
            text = NormalizeWhitespace(rawText);

        if (text.Length <= BertSearchDefaults.PreviewLength)
            return text;

        return text[..BertSearchDefaults.PreviewLength] + "...";
    }

    private static string ExtractUserText(string rawText)
    {
        var normalized = NormalizeNewlines(rawText);
        const string startMarker = "user_text:\n";
        const string endMarker = "\nfile_changes:";

        var startIndex = normalized.IndexOf(startMarker, StringComparison.Ordinal);
        if (startIndex < 0)
            return string.Empty;

        startIndex += startMarker.Length;
        var endIndex = normalized.IndexOf(endMarker, startIndex, StringComparison.Ordinal);
        if (endIndex < 0)
            endIndex = normalized.Length;

        return normalized[startIndex..endIndex].Trim();
    }

    private static string? ExtractGitCommit(string rawText)
    {
        var normalized = NormalizeNewlines(rawText);
        const string marker = "git_commit:";
        var markerIndex = normalized.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
            return null;

        markerIndex += marker.Length;
        var lineEnd = normalized.IndexOf('\n', markerIndex);
        if (lineEnd < 0)
            lineEnd = normalized.Length;

        var value = normalized[markerIndex..lineEnd].Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string NormalizeWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join(' ', NormalizeNewlines(value)
            .Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));
    }

    private static string NormalizeNewlines(string value)
    {
        return value.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
    }
}
