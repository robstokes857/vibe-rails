namespace VibeRails.DTOs;

/// <summary>Choices for a base CLI launch or its saved default. Never writes the user's CLI configuration.</summary>
public sealed record BaseLlmOptions(
    string? Model = null,
    string? Effort = null,
    string? Mode = null,
    bool Yolo = false,
    string? Speed = null);
