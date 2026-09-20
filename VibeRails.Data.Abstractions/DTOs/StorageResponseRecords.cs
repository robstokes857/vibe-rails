namespace VibeRails.DTOs;

public record SessionResponse(
    string Id,
    string Cli,
    string? EnvironmentName,
    string WorkingDirectory,
    DateTime StartedUTC,
    DateTime? EndedUTC,
    int? ExitCode
);

public record OpenSessionCleanupCandidate(
    string SessionId,
    int? OwnerPid
);

public record SessionLogResponse(
    long Id,
    string SessionId,
    DateTime Timestamp,
    string Content,
    bool IsError
);

public record SessionWithLogsResponse(
    SessionResponse Session,
    List<SessionLogResponse> Logs
);

public record UserInputRecord(
    long Id,
    string SessionId,
    int Sequence,
    string InputText,
    string? GitCommitHash,
    DateTime TimestampUTC
);

public record UnembeddedUserInputRow(
    long Id,
    string SessionId,
    string InputText
);

public record FileChangeInfo(
    string FilePath,
    string ChangeType,
    int? LinesAdded,
    int? LinesDeleted,
    string? DiffContent
);

public record SandboxDiffFileResponse(
    string FileName,
    string Language,
    string OriginalContent,
    string ModifiedContent
);

public record SandboxDiffResponse(
    List<SandboxDiffFileResponse> Files,
    int TotalChanges
);

public record BertFileChangeResponse(
    string FilePath,
    string ChangeType,
    int? LinesAdded,
    int? LinesDeleted
);

public record ChatHistoryItem(
    string Id,
    string Cli,
    string? EnvironmentName,
    string WorkingDirectory,
    string? ProjectDisplayName,
    DateTime StartedUTC,
    DateTime? EndedUTC,
    int? ExitCode,
    string? ParentSessionId,
    string? ParentCli,
    string? SessionDisplayName,
    int? Sequence,
    string? InputText,
    int UserInputCount,
    long? DurationSeconds
);

public record BoardAuthorDto(string Kind, string Label, string? Cli, string? SessionId = null);

public record BoardAttachmentDto(string Id, string Name, string Url, string MimeType, long Bytes, DateTime CreatedAt);
