namespace VibeRails.DTOs;

public record SessionLogChunkRecord(
    long Id,
    byte[] Content
);
