using VibeRails.Auth;
using VibeRails.Utils;

namespace VibeRails.Services;

/// <summary>
/// Single source of truth for whether remote access is fully configured and ready.
/// Also owns PIN set/verify/clear operations.
/// </summary>
public static class RemoteConfig
{
    /// <summary>
    /// True when remote access is enabled, an API key is configured, and a PIN is set.
    /// Use this everywhere instead of inline checks.
    /// </summary>
    public static bool IsEnabled =>
        ParserConfigs.GetRemoteAccess()
        && !string.IsNullOrWhiteSpace(ParserConfigs.GetApiKey())
        && IsPinConfigured;

    public static bool IsPinConfigured
    {
        get
        {
            var s = Config.LoadFresh();
            return !string.IsNullOrWhiteSpace(s.PinHash) && !string.IsNullOrWhiteSpace(s.PinSalt);
        }
    }

    public static bool VerifyPin(string input)
    {
        var s = Config.LoadFresh();
        if (string.IsNullOrWhiteSpace(s.PinHash) || string.IsNullOrWhiteSpace(s.PinSalt))
            return false;

        return Hasher.Verify(input, s.PinSalt, s.PinHash);
    }

    public static void SetPin(string pin)
    {
        var (salt, hash) = Hasher.Hash(pin);
        using var lease = Config.AcquireWriteLock();
        var s = Config.LoadFresh();
        s.PinSalt = salt;
        s.PinHash = hash;
        Config.Save(s);
    }

    public static void ClearPin()
    {
        using var lease = Config.AcquireWriteLock();
        var s = Config.LoadFresh();
        s.PinHash = string.Empty;
        s.PinSalt = string.Empty;
        Config.Save(s);
    }
}
