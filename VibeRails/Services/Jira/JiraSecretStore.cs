using System.Text.Json;
using System.Text.Json.Serialization;
using VibeRails.Utils;

namespace VibeRails.Services.Jira;

/// <summary>
/// The Jira API tokens for this machine, one per connection id. Same treatment as the app API
/// key in settings.json: plain text in the VibeRails data directory, which is locked to the
/// current user. A token is never a column in board.db, never a card field, and never returned
/// by the API. Nothing here logs it.
/// </summary>
public interface IJiraSecretStore
{
    bool HasToken(string connectionId);
    string? ReadToken(string connectionId);
    void SaveToken(string connectionId, string token);
    void DeleteToken(string connectionId);

    /// <summary>
    /// Deletes every token whose connection no longer exists. <paramref name="liveConnectionIds"/>
    /// is read while the file lock is held, so a token saved for a new connection (whose row is
    /// written before its token) is never pruned by a concurrent pass.
    /// </summary>
    Task PruneAsync(Func<CancellationToken, Task<IReadOnlyCollection<string>>> liveConnectionIds, CancellationToken cancellationToken);
}

public sealed class JiraSecretStore : IJiraSecretStore
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private readonly string _path;
    private readonly string _lockPath;
    // In-process gate first, then the OS file lock: several root backends (browser app, VS Code)
    // share this file, and each save is a read-modify-write of the whole map.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JiraSecretStore() : this(DefaultPath()) { }

    public JiraSecretStore(string path)
    {
        _path = path;
        _lockPath = path + ".lock";
    }

    public static string DefaultPath() =>
        Path.Combine(PathConstants.GetInstallDirPath(), "jira-tokens.json");

    public bool HasToken(string connectionId) =>
        Locked(() => ReadAll().ContainsKey(connectionId));

    public string? ReadToken(string connectionId) =>
        Locked(() => ReadAll().TryGetValue(connectionId, out var token) ? token : null);

    public void SaveToken(string connectionId, string token) =>
        Locked(() =>
        {
            var all = ReadAll();
            all[connectionId] = token;
            WriteAll(all);
            return true;
        });

    public void DeleteToken(string connectionId) =>
        Locked(() =>
        {
            var all = ReadAll();
            if (all.Remove(connectionId))
                WriteAll(all);
            return true;
        });

    public async Task PruneAsync(
        Func<CancellationToken, Task<IReadOnlyCollection<string>>> liveConnectionIds, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using var fileLock = CrossProcessFileLock.Acquire(_lockPath, LockTimeout);
            var all = ReadAll();
            if (all.Count == 0)
                return;
            var live = (await liveConnectionIds(cancellationToken)).ToHashSet(StringComparer.Ordinal);
            var orphans = all.Keys.Where(id => !live.Contains(id)).ToList();
            if (orphans.Count == 0)
                return;
            foreach (var id in orphans)
                all.Remove(id);
            WriteAll(all);
        }
        finally
        {
            _gate.Release();
        }
    }

    private T Locked<T>(Func<T> action)
    {
        _gate.Wait();
        try
        {
            using var fileLock = CrossProcessFileLock.Acquire(_lockPath, LockTimeout);
            return action();
        }
        finally
        {
            _gate.Release();
        }
    }

    private Dictionary<string, string> ReadAll()
    {
        if (!File.Exists(_path))
            return new Dictionary<string, string>(StringComparer.Ordinal);
        var json = File.ReadAllText(_path);
        return JsonSerializer.Deserialize(json, JiraSecretJsonContext.Default.DictionaryStringString)
            ?? new Dictionary<string, string>(StringComparer.Ordinal);
    }

    // Only ever called under the file lock, so the fixed temporary name cannot collide.
    private void WriteAll(Dictionary<string, string> tokens)
    {
        var directory = Path.GetDirectoryName(_path)!;
        PrivateFilePermissions.EnsureDirectory(directory);
        var json = JsonSerializer.Serialize(tokens, JiraSecretJsonContext.Default.DictionaryStringString);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, json);
        File.Move(temporary, _path, overwrite: true);
        PrivateFilePermissions.EnsureFile(_path);
    }
}

[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class JiraSecretJsonContext : JsonSerializerContext;
