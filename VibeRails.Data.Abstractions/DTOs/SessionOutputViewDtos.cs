namespace VibeRails.DTOs;

public record SessionOutputDetailResponse(
    string SessionId,
    string Cli,
    string? EnvironmentName,
    string WorkingDirectory,
    DateTime StartedUTC,
    DateTime? EndedUTC,
    bool Processed,
    string Text
);
