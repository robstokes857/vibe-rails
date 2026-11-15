using VibeRails.DB;

namespace VibeRails.Services.Launches;

/// <summary>Shortcut visibility only; never changes definitions, eligibility or Board pickers.</summary>
public sealed class QuickActionPreferences(IRepository repository)
{
    /// <summary>One row per shortcut avoids lost updates between projects or backend processes.</summary>
    public const string KeyPrefix = "ui.quick-actions.hidden.v1.";

    /// <summary>Reads this computer's hidden shortcuts, including temporarily unavailable items.</summary>
    public async Task<QuickActionPreferencesResponse> GetAsync(CancellationToken ct = default)
    {
        var cache = await repository.GetAllGlobalCacheAsync(ct);
        return new(cache.Where(pair => pair.Key.StartsWith(KeyPrefix, StringComparison.Ordinal) && pair.Value == "1")
            .Select(pair => pair.Key[KeyPrefix.Length..]).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>Saves one visibility choice without overwriting choices made in another window.</summary>
    public async Task SaveAsync(QuickActionPreferenceRequest request, CancellationToken ct = default)
    {
        var key = request.Key;
        var separator = key?.IndexOf(':') ?? -1;
        if (key is null || key.Length > 256 || separator <= 0 || separator == key.Length - 1
            || key[..separator] is not ("preset" or "action" or "script") || key.Any(char.IsControl))
            throw new ArgumentException("Choose a valid quick action.");
        await repository.SetGlobalCacheValueAsync(KeyPrefix + key, request.Hidden ? "1" : "0", ct);
    }
}

/// <summary>Machine-wide hidden shortcut identities, separate from launch permission.</summary>
public sealed record QuickActionPreferencesResponse(string[] HiddenKeys);

/// <summary>A single shortcut visibility change. Keys use definition type and stable ID.</summary>
public sealed record QuickActionPreferenceRequest(string Key, bool Hidden);
