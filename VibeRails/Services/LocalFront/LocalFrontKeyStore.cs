using System.Text.Json;
using System.Text.Json.Serialization;
using VibeRails.Services.HttpRelay;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;

namespace VibeRails.Services.LocalFront;

/// <summary>An API key issued by a local Front stack, with its approved display email.</summary>
public sealed record LocalFrontCredential(string ApiKey, string? AccountEmail);

/// <summary>
/// API keys for local Front stacks, one per normalized origin, in
/// <c>~/.vibe_rails/local-front-keys.json</c>. They are never written to settings.json, so a local
/// key cannot replace, be shown as, or be sent instead of the production key, and the production
/// key is never read here. Same file treatment as the production key and Jira tokens: plain text in
/// the data directory, which is private to the current user. Nothing here logs a key.
/// </summary>
public sealed class LocalFrontKeyStore
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private readonly string _path;
    private readonly string _lockPath;
    private readonly Lock _gate = new();

    /// <summary>Uses the per-user data directory.</summary>
    public LocalFrontKeyStore() : this(DefaultPath()) { }

    /// <summary>Uses <paramref name="path"/>; automated tests pass a disposable file.</summary>
    public LocalFrontKeyStore(string path)
    {
        _path = path;
        _lockPath = path + ".lock";
    }

    /// <summary>The key file beside settings.json.</summary>
    public static string DefaultPath() =>
        Path.Combine(PathConstants.GetInstallDirPath(), "local-front-keys.json");

    /// <summary>The credential saved for <paramref name="origin"/>, or null.</summary>
    public LocalFrontCredential? Read(string origin) =>
        Locked(() => ReadAll().TryGetValue(Key(origin), out var credential) && !string.IsNullOrWhiteSpace(credential.ApiKey)
            ? credential : null);

    /// <summary>
    /// Replaces the credential for <paramref name="origin"/> when the saved key still equals
    /// <paramref name="expectedApiKey"/> (empty for none). A blank <paramref name="apiKey"/> removes it.
    /// </summary>
    public bool TryReplace(string origin, string apiKey, string expectedApiKey, string? accountEmail) =>
        Locked(() =>
        {
            var all = ReadAll();
            var key = Key(origin);
            var current = all.TryGetValue(key, out var saved) ? saved.ApiKey : string.Empty;
            if (!string.Equals(current ?? string.Empty, expectedApiKey, StringComparison.Ordinal))
                return false;
            if (string.IsNullOrWhiteSpace(apiKey))
                all.Remove(key);
            else
                all[key] = new LocalFrontCredential(apiKey, accountEmail);
            WriteAll(all);
            return true;
        });

    /// <summary>Saves or removes the credential unconditionally (manual key edits in Settings).</summary>
    public void Set(string origin, string apiKey, string? accountEmail) =>
        Locked(() =>
        {
            var all = ReadAll();
            if (string.IsNullOrWhiteSpace(apiKey))
                all.Remove(Key(origin));
            else
                all[Key(origin)] = new LocalFrontCredential(apiKey, accountEmail);
            WriteAll(all);
            return true;
        });

    // Only normalized origins reach the file, so https://localhost:5164/ and its slashless form
    // share one entry.
    private static string Key(string origin) =>
        LocalFrontMode.TryParseOrigin(origin, out var parsed, out var error)
            ? parsed.GetLeftPart(UriPartial.Authority)
            : throw new ArgumentException(error, nameof(origin));

    private T Locked<T>(Func<T> action)
    {
        lock (_gate)
        {
            using var fileLock = CrossProcessFileLock.Acquire(_lockPath, LockTimeout);
            return action();
        }
    }

    private Dictionary<string, LocalFrontCredential> ReadAll()
    {
        if (!File.Exists(_path))
            return new(StringComparer.OrdinalIgnoreCase);
        var stored = JsonSerializer.Deserialize(File.ReadAllText(_path), LocalFrontKeyJsonContext.Default.DictionaryStringLocalFrontCredential);
        return stored is null
            ? new(StringComparer.OrdinalIgnoreCase)
            : new(stored, StringComparer.OrdinalIgnoreCase);
    }

    // Only ever called under the file lock, so the fixed temporary name cannot collide.
    private void WriteAll(Dictionary<string, LocalFrontCredential> credentials)
    {
        PrivateFilePermissions.EnsureDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(credentials, LocalFrontKeyJsonContext.Default.DictionaryStringLocalFrontCredential));
        PrivateFilePermissions.EnsureFile(temporary);
        File.Move(temporary, _path, overwrite: true);
        PrivateFilePermissions.EnsureFile(_path);
    }
}

/// <summary>
/// Device-link sign-in against the local Front: reads and saves the local key for the active
/// origin, and makes it this process's runtime key. The production settings key is untouched.
/// </summary>
public sealed class LocalFrontAccountKeyStore(LocalFrontKeyStore keys, string origin, IRemoteHttpRelayClient relay) : IRemoteAccountKeyStore
{
    /// <inheritdoc />
    public string Read() => keys.Read(origin)?.ApiKey ?? string.Empty;

    /// <inheritdoc />
    public string ComputerName
    {
        get
        {
            // The display name is a preference, not a credential: share it with normal runs.
            var name = ComputerNameFormatter.Normalize(Config.LoadFresh().ComputerName);
            return string.IsNullOrWhiteSpace(name) ? ComputerNameFormatter.Machine() : name;
        }
    }

    /// <inheritdoc />
    public bool TrySave(string apiKey, string expectedApiKey, string? accountEmail = null)
    {
        if (!keys.TryReplace(origin, apiKey, expectedApiKey, accountEmail))
            return false;
        ParserConfigs.SetApiKey(apiKey);
        relay.Reset();
        return true;
    }
}

[JsonSerializable(typeof(Dictionary<string, LocalFrontCredential>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class LocalFrontKeyJsonContext : JsonSerializerContext;
