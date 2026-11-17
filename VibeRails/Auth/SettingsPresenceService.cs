using System.Security.Cryptography;
using System.Text;
using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Auth;

/// <summary>Requires fresh dashboard confirmation for account and remote-control changes.</summary>
public sealed class SettingsPresenceService
{
    private readonly object gate = new();
    private string? code;
    private DateTime expires;
    private int failures;
    private DateTime retryAfter;

    /// <summary>Requests the current password or legacy PIN, or issues a single-use dashboard confirmation code.</summary>
    public SecurityChallengeResponse Challenge(bool pinRequired)
    {
        lock (gate)
        {
            if (pinRequired) return new(true, null);
            code = Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
            expires = DateTime.UtcNow.AddMinutes(2);
            return new(false, code);
        }
    }

    internal bool Verify(string? proof, Settings settings)
    {
        lock (gate)
        {
            if (DateTime.UtcNow < retryAfter || !Services.RemoteConfig.IsCredentialWithinBounds(proof)) return false;
            // Must match the Challenge caller's RemoteConfig.IsPinConfigured.
            var pinRequired = Services.RemoteConfig.HasCredential(settings);
            var accepted = pinRequired ? Hasher.Verify(proof, settings.PinSalt, settings.PinHash)
                : code is not null && DateTime.UtcNow < expires && CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(proof), Encoding.UTF8.GetBytes(code));
            if (accepted)
            {
                code = null;
                failures = 0;
                return true;
            }
            if (++failures >= 5)
            {
                retryAfter = DateTime.UtcNow.AddMinutes(5);
                failures = 0;
                code = null;
            }
            return false;
        }
    }
}
