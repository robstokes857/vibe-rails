namespace VibeRails.DTOs;

public record SessionLogChunkRecord(
    long Id,
    DateTime TimestampUtc,
    byte[] Content
);
