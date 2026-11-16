using VibeRails.DB;

namespace VibeRails.Services.Launches;

/// <summary>Shortcut visibility only; never changes definitions, eligibility or Board pickers.</summary>
public sealed class QuickActionPreferences(IRepository repository)
{
    /// <summary>One row per shortcut avoids lost updates between projects or backend processes.</summary>
    public const string KeyPrefix = "ui.quick-actions.hidden.v1.";

    /// <summary>Reads this computer's hidden shortcuts, including temporarily unavailable items.</summary>
    public async Task<QuickActionPreferencesResponse> GetAsync(CancellationToken ct = default) =>
        new(await HiddenItemPreferences.HiddenKeysAsync(repository, KeyPrefix, ct));

    /// <summary>Saves one visibility choice without overwriting choices made in another window.</summary>
    public Task SaveAsync(QuickActionPreferenceRequest request, CancellationToken ct = default) =>
        HiddenItemPreferences.SaveAsync(repository, KeyPrefix, request.Key, request.Hidden,
            ["preset", "action", "script"], "Choose a valid quick action.", ct);
}

/// <summary>
/// What the Board lane <b>Add to this lane</b> picker offers (VIBE-337). Hiding an entry only removes it from
/// that picker: it never disables or retires the definition, removes it from lanes that already run it, or
/// changes Quick actions, AI Agents or the LLM pickers. Keys are <c>preset:&lt;id&gt;</c> and
/// <c>action:&lt;id&gt;</c> for saved definitions, and <c>check:vca</c> / <c>check:code-quality</c> for the
/// built-in new checks. A key whose definition is gone stays hidden harmlessly.
/// </summary>
public sealed class LanePickerPreferences(IRepository repository)
{
    /// <summary>One row per entry, like Quick actions, so two windows never overwrite each other's choices.</summary>
    public const string KeyPrefix = "ui.lane-picker.hidden.v1.";

    /// <summary>Reads this computer's hidden lane picker entries.</summary>
    public async Task<LanePickerPreferencesResponse> GetAsync(CancellationToken ct = default) =>
        new(await HiddenItemPreferences.HiddenKeysAsync(repository, KeyPrefix, ct));

    /// <summary>Saves one entry's visibility.</summary>
    public Task SaveAsync(LanePickerPreferenceRequest request, CancellationToken ct = default) =>
        HiddenItemPreferences.SaveAsync(repository, KeyPrefix, request.Key, request.Hidden,
            ["preset", "action", "check"], "Choose a valid lane picker entry.", ct);
}

/// <summary>
/// Per-item show/hide choices for one picker: one <c>GlobalCache</c> row per item in the normal
/// <c>state.db</c>, so choices made in different windows, projects or backend processes never overwrite
/// each other. Missing rows mean visible. Visibility only; nothing here reads or writes definitions.
/// </summary>
internal static class HiddenItemPreferences
{
    /// <summary>The hidden keys under <paramref name="prefix"/>, ordinal order.</summary>
    public static async Task<string[]> HiddenKeysAsync(IRepository repository, string prefix, CancellationToken ct)
    {
        var cache = await repository.GetAllGlobalCacheAsync(ct);
        return cache.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal) && pair.Value == "1")
            .Select(pair => pair.Key[prefix.Length..]).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Validates <c>&lt;kind&gt;:&lt;id&gt;</c> against <paramref name="kinds"/> before writing anything.</summary>
    public static async Task SaveAsync(IRepository repository, string prefix, string? key, bool hidden,
        string[] kinds, string invalidMessage, CancellationToken ct)
    {
        var separator = key?.IndexOf(':') ?? -1;
        if (key is null || key.Length > 256 || separator <= 0 || separator == key.Length - 1
            || !kinds.Contains(key[..separator], StringComparer.Ordinal) || key.Any(char.IsControl))
            throw new ArgumentException(invalidMessage);
        await repository.SetGlobalCacheValueAsync(prefix + key, hidden ? "1" : "0", ct);
    }
}

/// <summary>Machine-wide hidden shortcut identities, separate from launch permission.</summary>
public sealed record QuickActionPreferencesResponse(string[] HiddenKeys);

/// <summary>A single shortcut visibility change. Keys use definition type and stable ID.</summary>
public sealed record QuickActionPreferenceRequest(string Key, bool Hidden);

/// <summary>Entries hidden from the Board lane picker on this computer (VIBE-337).</summary>
public sealed record LanePickerPreferencesResponse(string[] HiddenKeys);

/// <summary>A single lane picker visibility change. Keys use definition type and stable ID, or <c>check:&lt;kind&gt;</c>.</summary>
public sealed record LanePickerPreferenceRequest(string Key, bool Hidden);
