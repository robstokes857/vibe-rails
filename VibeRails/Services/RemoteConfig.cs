using VibeRails.Auth;
using VibeRails.Utils;

namespace VibeRails.Services;

/// <summary>
/// Single source of truth for whether remote access is fully configured and ready.
/// Also owns PIN set/verify/clear operations.
/// </summary>
public static class RemoteConfig
{
    // Shared with presence proofs. This is a transport/work bound, not a strength policy;
    // setup/change strength is deliberately enforced only by the frontend.
    internal const int MaxCredentialLength = 128;

    internal static bool IsCredentialWithinBounds([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? credential) =>
        credential is { Length: > 0 and <= MaxCredentialLength };

    /// <summary>
    /// A new credential must also contain a non-whitespace character: <see cref="Hasher.Hash"/>
    /// rejects whitespace-only input, so it has to be refused before a presence proof is spent.
    /// Surrounding whitespace is kept and verified exactly; this is not a strength rule.
    /// </summary>
    internal static bool IsSettableCredential([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? credential) =>
        IsCredentialWithinBounds(credential) && !string.IsNullOrWhiteSpace(credential);

    /// <summary>
    /// The one definition of "a password or PIN is set". The status route, the presence challenge
    /// and its verification must agree, or a damaged settings file can lock the user out of both
    /// setting and clearing the credential.
    /// </summary>
    internal static bool HasCredential(Settings settings) =>
        !string.IsNullOrWhiteSpace(settings.PinHash) && !string.IsNullOrWhiteSpace(settings.PinSalt);

    /// <summary>
    /// True when remote access is enabled, an API key is configured, and a PIN is set.
    /// Use this everywhere instead of inline checks.
    /// </summary>
    public static bool IsEnabled =>
        ParserConfigs.GetRemoteAccess()
        && !string.IsNullOrWhiteSpace(ParserConfigs.GetApiKey())
        && IsPinConfigured;

    public static bool IsPinConfigured => HasCredential(Config.LoadFresh());

    public static bool VerifyPin(string input) => VerifyPin(input, Config.Store);

    internal static bool VerifyPin(string input, SettingsFile store)
    {
        if (!IsCredentialWithinBounds(input)) return false;
        var s = store.LoadFresh();
        if (!HasCredential(s))
            return false;

        return Hasher.Verify(input, s.PinSalt, s.PinHash);
    }

    public static void SetPin(string pin) => SetPin(pin, Config.Store);

    internal static void SetPin(string pin, SettingsFile store)
    {
        var (salt, hash) = Hasher.Hash(pin);
        using var lease = store.AcquireWriteLock();
        var s = store.LoadFresh();
        s.PinSalt = salt;
        s.PinHash = hash;
        store.Save(s);
    }

    public static void ClearPin() => ClearPin(Config.Store);

    internal static void ClearPin(SettingsFile store)
    {
        using var lease = store.AcquireWriteLock();
        var s = store.LoadFresh();
        s.PinHash = string.Empty;
        s.PinSalt = string.Empty;
        store.Save(s);
    }
}
