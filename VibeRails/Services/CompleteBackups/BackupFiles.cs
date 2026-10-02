using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VibeRails.Data.Abstractions;
using VibeRails.DB;
using VibeRails.Utils;

namespace VibeRails.Services.Backups;

/// <summary>Enumerates durable local files without following links or traversing arbitrary project directories.</summary>
public sealed class BackupFiles(IServiceScopeFactory scopes)
{
    public static readonly List<string> Exclusions =
    [
        "Credentials: settings/config JSON credential fields; auth/credential/token/secret files, .env files, .claude.json, script-signing approvals/PINs, private keys, signing keys and OS keychains. Reconnect accounts and reapprove scripts after restore.",
        "Derived files: node_modules, .git, caches, logs, tmp, telemetry, binaries, models, embeddings and mining databases. Derived tables inside state.db remain in its complete snapshot.",
        "External files: repositories, sandboxes, dependencies, arbitrary @path references and script imports are outside coverage. Only referenced Automation script files and configured/native CLI homes are included.",
        "File links and inaccessible/unstable files are reported as incomplete. Separate datasets have separate snapshot times. Native history includes only bytes retained locally; previously dropped or deleted captures cannot be recovered.",
        "Database content, prompts, history, scripts and text configuration may contain embedded secrets. These private account archives preserve that content; filename filtering is not a general secret scrubber."
    ];

    public async Task<List<BackupFileSource>> EnumerateAsync(string root, CancellationToken ct)
    {
        var result = new List<BackupFileSource>();
        foreach (var file in Directory.EnumerateFiles(root).Order(StringComparer.Ordinal))
            if (Path.GetExtension(file).ToLowerInvariant() is ".json" or ".toml" or ".yaml" or ".yml" or ".md")
                Add(file, "installation/" + Path.GetFileName(file));
        AddTree(Path.Combine(root, "scripts"), "installation/scripts");
        AddTree(Path.Combine(root, "envs"), "installation/envs");
        AddTree(Path.Combine(root, "history"), "installation/history");

        using var scope = scopes.CreateScope();
        var envs = await scope.ServiceProvider.GetRequiredService<IEnvironmentStore>().GetAllEnvironmentsAsync(ct);
        foreach (var env in envs.Where(e => !string.IsNullOrWhiteSpace(e.Path)))
            if (!IsWithin(Path.Combine(root, "envs"), env.Path)) AddTree(env.Path, $"environments/{env.Id}", required: true);
        var jobs = await scope.ServiceProvider.GetRequiredService<IJobStore>().GetJobsAsync(includeDeleted: true, cancellationToken: ct);
        foreach (var job in jobs)
            foreach (var action in (job.Actions ?? []).Where(a => !string.IsNullOrWhiteSpace(a.ScriptPath)))
            {
                var path = Path.GetFullPath(action.ScriptPath!, job.ProjectPath);
                // Saved actions are repository relative; a corrupt/legacy path must not expand the backup boundary.
                if (!IsWithin(job.ProjectPath, path))
                    result.Add(new(path, $"automation/{job.Id}/{action.Id}", "Referenced script is outside its repository."));
                else Add(path, $"automation/{job.Id}/{action.Id}/{Path.GetFileName(path)}");
            }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var name in new[] { ".codex", ".claude", ".copilot", ".opencode", ".grok", ".gemini" })
            AddTree(Path.Combine(home, name), "native/" + name);
        if (File.Exists(Path.Combine(home, ".claude.json"))) Add(Path.Combine(home, ".claude.json"), "native/.claude.json");
        AddTree(Path.Combine(home, ".local", "share", "opencode"), "native/opencode-data");
        AddTree(Path.Combine(home, ".config", "opencode"), "native/opencode-config");
        return result.OrderBy(f => f.Entry, StringComparer.Ordinal).ToList();

        void Add(string path, string entry)
        {
            ct.ThrowIfCancellationRequested();
            if (Excluded(Path.GetFileName(path))) return;
            result.Add(new(path, entry, HasLink(path) ? "Symbolic link or junction excluded." : null));
        }

        void AddTree(string directory, string entry, bool required = false)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory))
            {
                if (required) result.Add(new(directory, entry, "Configured environment directory is missing."));
                return;
            }
            if (HasLink(directory)) { result.Add(new(directory, entry, "Symbolic link or junction excluded.")); return; }
            try
            {
                foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
                {
                    if (Excluded(Path.GetFileName(path))) continue;
                    var child = entry + "/" + Path.GetFileName(path);
                    if (Directory.Exists(path)) AddTree(path, child); else Add(path, child);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { result.Add(new(directory, entry, "Directory could not be read.")); }
        }
    }

    internal static bool IsWithin(string root, string path) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static bool HasLink(string path)
    {
        for (var p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return true;
        return false;
    }

    internal static bool Excluded(string name)
    {
        var n = name.ToLowerInvariant();
        return n is "node_modules" or ".git" or "cache" or "caches" or ".cache" or "tmp" or "temp" or "logs" or "log" or "telemetry" or "bin" or "models" or "signing-keys"
            || n.StartsWith(".env") || n.Contains("credential") || n.Contains("secret") || n.Contains("token")
            || n is "script_signing.json" or "oauth_creds.json" or "accounts.json" or ".claude.json" || n.StartsWith("auth.")
            || n.StartsWith("appsettings") || n.StartsWith("vb.")
            || n.EndsWith(".pem") || n.EndsWith(".key") || n.EndsWith(".pfx") || n.EndsWith(".p12")
            || n.EndsWith(".lock") || n.EndsWith(".log") || n.EndsWith(".exe") || n.EndsWith(".dll")
            || n.EndsWith("-wal") || n.EndsWith("-shm") || n.EndsWith("-journal");
    }

    internal static string FileStamp(string path)
    {
        var f = new FileInfo(path);
        return f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "missing";
    }

    internal static string Fingerprint(IEnumerable<BackupFileSource> files) => BackupFormat.Hash(Encoding.UTF8.GetBytes(
        string.Join('\n', files.Select(f => $"{f.Entry}|{f.Path}|{f.Issue}|{FileStamp(f.Path)}|{FileStamp(f.Path + "-wal")}"))));

    internal static async Task<List<string>> WriteZipAsync(Stream output, IReadOnlyList<BackupFileSource> sources,
        IDatabaseSnapshotStore snapshots, string work, CancellationToken ct)
    {
        var issues = new List<string>();
        var inventory = new List<BackupFileEntry>();
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (source.Issue is not null) { Issue(source.Issue); continue; }
            string? temporary = null;
            try
            {
                if (HasLink(source.Path)) { Issue("File became a link."); continue; }
                var stamp = FileStamp(source.Path);
                var path = source.Path;
                if (Path.GetExtension(path).ToLowerInvariant() is ".db" or ".sqlite" or ".sqlite3")
                {
                    temporary = Path.Combine(work, "native-" + Guid.NewGuid().ToString("N") + ".db");
                    await snapshots.CreateSnapshotAsync(path, temporary, ct);
                    path = temporary;
                }
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var entry = zip.CreateEntry(source.Entry, CompressionLevel.Fastest).Open();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long bytes = 0;
                if (Path.GetFileName(source.Path).ToLowerInvariant() is "settings.json" or "config.json")
                {
                    using var doc = await JsonDocument.ParseAsync(input, cancellationToken: ct);
                    using var buffer = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(buffer))
                    {
                        WriteSettings(doc.RootElement, writer);
                    }
                    var data = buffer.ToArray();
                    await entry.WriteAsync(data, ct); hash.AppendData(data); bytes = data.Length;
                }
                else
                {
                    var buffer = new byte[128 * 1024];
                    int read;
                    while ((read = await input.ReadAsync(buffer, ct)) > 0)
                    { await entry.WriteAsync(buffer.AsMemory(0, read), ct); hash.AppendData(buffer, 0, read); bytes += read; }
                }
                inventory.Add(new(source.Entry, source.Path, bytes, Convert.ToHexStringLower(hash.GetHashAndReset())));
                if (temporary is null && stamp != FileStamp(source.Path)) Issue("File changed during capture; retry required.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or StorageException)
            { Issue("File could not be completely captured."); }
            finally { if (temporary is not null && File.Exists(temporary)) File.Delete(temporary); }

            void Issue(string message)
            {
                if (issues.Count < 999) issues.Add($"{source.Entry}: {message}"[..Math.Min(source.Entry.Length + message.Length + 2, 1000)]);
                else if (issues.Count == 999) issues.Add("Further file errors omitted; this archive is incomplete.");
            }
        }
        await using var manifest = zip.CreateEntry("files.json").Open();
        await JsonSerializer.SerializeAsync(manifest, inventory, BackupFilesJson.Default.ListBackupFileEntry, ct);
        return issues;
    }

    private static void WriteSettings(JsonElement value, Utf8JsonWriter writer)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var p in value.EnumerateObject())
            {
                var name = p.Name.Replace("_", "").Replace("-", "").ToLowerInvariant();
                if (name.Contains("apikey") || name.EndsWith("token") || name == "tokens" || name.Contains("password") || name.Contains("secret")
                    || name is "pinhash" or "pinsalt" or "remoteaccountemail" or "remoteaccountkeyfingerprint" or "authorization") continue;
                writer.WritePropertyName(p.Name); WriteSettings(p.Value, writer);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteSettings(item, writer); writer.WriteEndArray(); }
        else value.WriteTo(writer);
    }
}

public sealed record BackupFileSource(string Path, string Entry, string? Issue = null);
public sealed record BackupFileEntry(string Entry, string OriginalPath, long Bytes, string Sha256);
[System.Text.Json.Serialization.JsonSerializable(typeof(List<BackupFileEntry>))]
internal partial class BackupFilesJson : System.Text.Json.Serialization.JsonSerializerContext;
