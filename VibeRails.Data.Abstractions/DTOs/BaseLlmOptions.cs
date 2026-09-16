namespace VibeRails.DTOs;

/// <summary>Session-only choices for a base CLI. Never writes the user's CLI configuration.</summary>
public sealed record BaseLlmOptions(string? Model = null, string? Effort = null, string? Mode = null);
