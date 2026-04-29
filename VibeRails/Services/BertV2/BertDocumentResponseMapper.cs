using VibeRails.DTOs;

namespace VibeRails.Services.BertV2;

public sealed class BertDocumentResponseMapper : IBertDocumentResponseMapper
{
    public BertCaptureSummaryResponse ToCaptureSummary(BertStoredDocument document, BertInputMetadata? metadata)
    {
        var parsedId = BertDocumentId.Parse(document.DocumentId);

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
            BuildPreview(metadata?.InputText, document.RawText),
            metadata?.FileChangeCount ?? 0);
    }

    public BertCaptureDetailResponse ToCaptureDetail(BertStoredDocument document, BertInputMetadata? metadata, IReadOnlyList<BertFileChangeResponse> fileChanges)
    {
        var parsedId = BertDocumentId.Parse(document.DocumentId);

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
            metadata?.InputText ?? ExtractUserText(document.RawText),
            fileChanges.ToList(),
            NormalizeNewlines(document.RawText));
    }

    public BertSearchHitResponse ToSearchHit(BertStoredDocument document, BertInputMetadata? metadata)
    {
        var parsedId = BertDocumentId.Parse(document.DocumentId);

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
            BuildPreview(metadata?.InputText, document.RawText),
            metadata?.FileChangeCount ?? 0,
            document.Score);
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
