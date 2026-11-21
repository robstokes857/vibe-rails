using System.Security.Cryptography;
using System.Text;
using VibeRails.Services.HttpRelay;
using VibeRails.Utils;

namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>Persists an approved key only while the original saved key is still current.</summary>
public interface IRemoteAccountKeyStore
{
    /// <summary>Reads the current saved credential.</summary>
    string Read();
    /// <summary>Returns the display name sent with a link request.</summary>
    string ComputerName { get; }
    /// <summary>Saves and activates the new credential, or rejects a concurrent key edit.</summary>
    bool TrySave(string apiKey, string expectedApiKey, string? accountEmail = null);
}

/// <summary>Uses the same settings file and runtime credential as a manually pasted key.</summary>
public sealed class ApiKeyStore : IRemoteAccountKeyStore
{
    private readonly IRemoteHttpRelayClient _relay;
    private readonly SettingsFile _settings;

    /// <summary>Uses the shared application settings file.</summary>
    public ApiKeyStore(IRemoteHttpRelayClient relay) : this(relay, Config.Store) { }

    // Automated tests exercise the same compare/save against disposable settings files.
    internal ApiKeyStore(IRemoteHttpRelayClient relay, SettingsFile settings)
    {
        _relay = relay;
        _settings = settings;
    }

    /// <inheritdoc />
    public string Read() => _settings.LoadFresh().ApiKey;

    /// <inheritdoc />
    public string ComputerName
    {
        get
        {
            var name = ComputerNameFormatter.Normalize(_settings.LoadFresh().ComputerName);
            return string.IsNullOrWhiteSpace(name) ? ComputerNameFormatter.Machine() : name;
        }
    }

    /// <inheritdoc />
    public bool TrySave(string apiKey, string expectedApiKey, string? accountEmail = null)
    {
        using (_settings.AcquireWriteLock())
        {
            var settings = _settings.LoadFresh();
            // A retry finds the approved key already in the file when an earlier attempt wrote it
            // and then failed. That is this sign-in, not a competing edit: reporting "key changed"
            // here told the user a completed sign-in had failed.
            var alreadySaved = string.Equals(settings.ApiKey, apiKey, StringComparison.Ordinal);
            if (!alreadySaved && !string.Equals(settings.ApiKey, expectedApiKey, StringComparison.Ordinal))
                return false;
            var fingerprint = Fingerprint(apiKey);
            if (!alreadySaved || settings.RemoteAccountEmail != accountEmail
                || settings.RemoteAccountKeyFingerprint != fingerprint)
            {
                settings.ApiKey = apiKey;
                settings.RemoteAccountEmail = accountEmail;
                settings.RemoteAccountKeyFingerprint = fingerprint;
                _settings.Save(settings);
            }
            ParserConfigs.SetApiKey(apiKey);
            // The key is saved and active. The relay reconnects with the current key on its next
            // request anyway, so a failed reset must not turn the saved key into a failed sign-in.
            try { _relay.Reset(); }
            catch (Exception ex)
            {
                Serilog.Log.Warning("[Account] Could not reset the HTTP relay after saving the account key ({ExceptionType})",
                    ex.GetType().Name);
            }
            return true;
        }
    }

    // Older versions/manual edits can replace the key without clearing the display metadata.
    // Compare the full credential, never its masked suffix, before returning an identity.
    internal static string? GetAccountEmail(Settings settings) =>
        !string.IsNullOrWhiteSpace(settings.ApiKey)
        && settings.RemoteAccountKeyFingerprint == Fingerprint(settings.ApiKey)
            ? settings.RemoteAccountEmail : null;

    /// <summary>
    /// <see cref="GetAccountEmail"/> for a caller that asks on every poll (the review inbox, VIBE-213 item 17):
    /// the settings file is re-read only when <paramref name="apiKey"/> answers a different key. The identity
    /// is returned only while the file holds that same key (another root may have switched accounts), and only
    /// then kept, so a file that has not caught up is read again next time.
    /// </summary>
    internal static Func<string?> CachedAccountEmail(Func<string?> apiKey, Func<Settings> loadFresh)
    {
        AccountEmailEntry? cached = null;
        return () =>
        {
            var key = apiKey();
            if (cached is { } hit && hit.Key == key) return hit.Email;
            var settings = loadFresh();
            if (settings.ApiKey != key) return null;
            var email = GetAccountEmail(settings);
            cached = new AccountEmailEntry(key, email);
            return email;
        };
    }

    private sealed record AccountEmailEntry(string? Key, string? Email);

    private static string Fingerprint(string apiKey) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)));
}
