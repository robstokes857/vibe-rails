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
    bool TrySave(string apiKey, string expectedApiKey);
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
    public bool TrySave(string apiKey, string expectedApiKey)
    {
        using (_settings.AcquireWriteLock())
        {
            var settings = _settings.LoadFresh();
            if (!string.Equals(settings.ApiKey, expectedApiKey, StringComparison.Ordinal))
                return false;
            settings.ApiKey = apiKey;
            _settings.Save(settings);
            ParserConfigs.SetApiKey(apiKey);
            _relay.Reset();
            return true;
        }
    }
}
