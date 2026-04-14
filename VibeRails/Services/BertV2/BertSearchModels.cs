namespace VibeRails.Services.BertV2;

public sealed record BertStoredDocument(string DocumentId, string RawText, double? Score);

public sealed record BertInputMetadata(
    string DocumentId,
    string SessionId,
    long UserInputId,
    int Sequence,
    string InputText,
    string? GitCommitHash,
    DateTime TimestampUtc,
    string? Cli,
    string? EnvironmentName,
    string? WorkingDirectory,
    int FileChangeCount);

internal sealed record BertParsedDocumentId(string? SessionId, long? UserInputId);
