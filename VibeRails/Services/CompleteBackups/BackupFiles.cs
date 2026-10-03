using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VibeRails.Data.Abstractions;
using VibeRails.DB;
using VibeRails.Utils;

namespace VibeRails.Services.Backups;

/// <summary>Enumerates durable local files without following links or traversing arbitrary project directories.</summary>
public sealed partial class BackupFiles(IServiceScopeFactory scopes)
{
    public static readonly List<string> Exclusions =
    [
        "Credentials: JSON/TOML credential fields, MCP env/headers blocks, header/environment argument values and credential arguments; auth/oauth/credential/token/secret/accounts files, .env files, .claude.json and its backups, browser profiles, script-signing approvals/PINs, private keys, signing keys and OS keychains. Reconnect accounts and reapprove scripts after restore.",
        "Derived files: node_modules, .git, caches, logs, tmp, telemetry, binaries, models, embeddings and mining databases. Derived tables inside state.db remain in its complete snapshot.",
        "CLI runtime state: native session transcripts, prompt history, CLI log/history databases, file-history checkpoints, shell snapshots and downloaded packages are not configuration and are excluded from CLI homes. VibeRails sessions remain in state.db.",
        "External files: repositories, sandboxes, dependencies, arbitrary @path references and script imports are outside coverage. Only referenced Automation script files and configured/native CLI homes are included.",
        "File links and inaccessible/unstable files are reported as incomplete. Separate datasets have separate snapshot times. Native history includes only bytes retained locally; previously dropped or deleted captures cannot be recovered.",
        "Database content, prompts, history, scripts and text configuration may contain embedded secrets. These private account archives preserve that content; filename filtering is not a general secret scrubber."
    ];

    /// <summary>Files up to this size are read into memory before their zip entry exists; larger ones are staged on disk.</summary>
    internal const int InMemoryCaptureBytes = 16 * 1024 * 1024;

    public Task<List<BackupFileSource>> EnumerateAsync(string root, CancellationToken ct) =>
        EnumerateAsync(root, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ct);

    /// <summary>Enumerates with an explicit user profile so tests can drive the native-home filters against a fixture tree.</summary>
    internal async Task<List<BackupFileSource>> EnumerateAsync(string root, string home, CancellationToken ct)
    {
        var result = new List<BackupFileSource>();
        using var scope = scopes.CreateScope();
        // Every configured CLI home is captured as its own required tree. The name filter never applies to it
        // or its ancestors: an environment called "Auth review" or "token-cache" is a label, not a credential
        // store. Homes under envs/ keep their installation/envs entry names; the envs/ walk skips them.
        var envRoot = Path.Combine(root, "envs");
        var homes = new Dictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var env in (await scope.ServiceProvider.GetRequiredService<IEnvironmentStore>().GetAllEnvironmentsAsync(ct))
            .Where(e => !string.IsNullOrWhiteSpace(e.Path)))
        {
            // A relative stored path would resolve against the process directory, not a configured home.
            if (!Path.IsPathFullyQualified(env.Path)) { result.Add(new(env.Path, $"environments/{env.Id}", "Configured environment directory is not an absolute path.")); continue; }
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(env.Path));
            homes.TryAdd(full, IsWithin(envRoot, full) ? "installation/envs/" + Path.GetRelativePath(envRoot, full).Replace('\\', '/') : $"environments/{env.Id}");
        }

        foreach (var file in Directory.EnumerateFiles(root).Order(StringComparer.Ordinal))
            if (Path.GetExtension(file).ToLowerInvariant() is ".json" or ".toml" or ".yaml" or ".yml" or ".md")
                Add(file, "installation/" + Path.GetFileName(file));
        AddTree(Path.Combine(root, "scripts"), "installation/scripts");
        AddTree(envRoot, "installation/envs", cliHome: true);
        AddTree(Path.Combine(root, "history"), "installation/history");
        foreach (var (configured, entry) in homes) AddTree(configured, entry, required: true, cliHome: true);

        var jobs = await scope.ServiceProvider.GetRequiredService<IJobStore>().GetJobsAsync(includeDeleted: true, cancellationToken: ct);
        foreach (var job in jobs)
            foreach (var action in (job.Actions ?? []).Where(a => !string.IsNullOrWhiteSpace(a.ScriptPath)))
            {
                var entry = $"automation/{job.Id}/{action.Id}";
                string path;
                try
                {
                    if (!Path.IsPathFullyQualified(job.ProjectPath ?? "")) { result.Add(new(job.ProjectPath ?? "", entry, "Automation repository path is not absolute.")); continue; }
                    path = Path.GetFullPath(action.ScriptPath!, job.ProjectPath!);
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                { result.Add(new(action.ScriptPath!, entry, "Referenced script path is invalid.")); continue; }
                // Saved actions are repository relative; a corrupt/legacy path must not expand the backup boundary.
                if (!IsWithin(job.ProjectPath!, path))
                    result.Add(new(path, entry, "Referenced script is outside its repository."));
                // An explicitly referenced, approved script is captured whatever its name (rotate_token.py,
                // cache_warm.py): the name filter exists for discovered credential stores, and dropping a
                // required file silently would report coverage the archive does not have.
                else Add(path, $"{entry}/{Path.GetFileName(path)}", required: true);
            }

        // ~/.claude.json is deliberately absent: it is a credential/account file and Excluded() rejects it by name.
        foreach (var name in new[] { ".codex", ".claude", ".copilot", ".opencode", ".grok", ".gemini" })
            AddTree(Path.Combine(home, name), "native/" + name, cliHome: true);
        AddTree(Path.Combine(home, ".local", "share", "opencode"), "native/opencode-data", cliHome: true);
        AddTree(Path.Combine(home, ".config", "opencode"), "native/opencode-config", cliHome: true);
        return result.OrderBy(f => f.Entry, StringComparer.Ordinal).ToList();

        void Add(string path, string entry, bool required = false)
        {
            ct.ThrowIfCancellationRequested();
            if (!required && Excluded(Path.GetFileName(path))) return;
            if (required && !File.Exists(path)) { result.Add(new(path, entry, "Referenced script is missing.")); return; }
            result.Add(new(path, entry, HasLink(path) ? "Symbolic link or junction excluded." : null));
        }

        void AddTree(string directory, string entry, bool required = false, bool cliHome = false)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(directory))
            {
                if (required) result.Add(new(directory, entry, "Configured environment directory is missing."));
                return;
            }
            // The whole ancestor chain is checked once per tree; below it, each entry's own attributes suffice.
            if (HasLink(directory)) { result.Add(new(directory, entry, "Symbolic link or junction excluded.")); return; }
            Walk(new DirectoryInfo(directory), entry, cliHome);
        }

        void Walk(DirectoryInfo directory, string entry, bool cliHome)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Enumeration already carries each entry's attributes, so no per-file stat is needed.
                foreach (var info in directory.EnumerateFileSystemInfos().OrderBy(i => i.Name, StringComparer.Ordinal))
                {
                    var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                    if (Excluded(info.Name) || (cliHome && IsCliRuntimeState(info.Name, isDirectory))
                        || (isDirectory && homes.ContainsKey(info.FullName))) continue;
                    var child = entry + "/" + info.Name;
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) result.Add(new(info.FullName, child, "Symbolic link or junction excluded."));
                    else if (isDirectory) Walk((DirectoryInfo)info, child, cliHome);
                    else result.Add(new(info.FullName, child));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { result.Add(new(directory.FullName, entry, "Directory could not be read.")); }
        }
    }

    internal static bool IsWithin(string root, string path) => Path.GetFullPath(path).StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    internal static bool HasLink(string path) => HasLink(path, null);

    /// <summary>
    /// True when the path or any ancestor is a symbolic link or junction. <paramref name="directories"/> caches
    /// verdicts for ancestor directories across one capture pass, so sibling files do not re-walk the same chain.
    /// </summary>
    private static bool HasLink(string path, Dictionary<string, bool>? directories)
    {
        var full = Path.GetFullPath(path);
        if (IsLink(full)) return true;
        var parent = Path.GetDirectoryName(full);
        if (parent is null) return false;
        if (directories is null) return HasLink(parent, null);
        if (!directories.TryGetValue(parent, out var linked)) directories[parent] = linked = HasLink(parent, directories);
        return linked;
    }

    private static bool IsLink(string path) =>
        (File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// Name-based credential and derived-file filter. Credential stores are matched as a family, not a list:
    /// any <c>auth</c>/<c>oauth</c>/<c>creds</c>/<c>accounts</c> name segment (<c>auth.json</c>, <c>mcp-auth.json</c>,
    /// <c>oauth_creds.json</c>, <c>mcp-oauth-tokens.json</c>, <c>google_accounts.json</c>), any name containing
    /// <c>credential</c>/<c>secret</c>/<c>token</c>, <c>.claude.json</c> and its <c>.claude.json.backup.*</c> copies,
    /// and Chromium profile state (browser directories, <c>Cookies</c>, <c>Login Data</c>, <c>Web Data</c>,
    /// <c>Local State</c>) are rejected. A whole word is required for the short segments so <c>author.md</c> or
    /// <c>authorize_pr.py</c> are still captured.
    /// </summary>
    internal static bool Excluded(string name)
    {
        var n = name.ToLowerInvariant();
        return n is "node_modules" or ".git" or "tmp" or "temp" or "logs" or "log" or "telemetry" or "bin" or "models" or "signing-keys"
            || n.Contains("cache")
            || n.StartsWith(".env") || n.Contains("credential") || n.Contains("secret") || n.Contains("token")
            || HasSegment(n, "auth") || HasSegment(n, "oauth") || HasSegment(n, "creds") || HasSegment(n, "accounts")
            || n is "script_signing.json" or "cookies" or "login data" or "web data" or "local state" || n.StartsWith(".claude.json")
            || n.Contains("browser") || n is "chrome" or "chromium"
            || n.StartsWith("appsettings") || n.StartsWith("vb.")
            || n.EndsWith(".pem") || n.EndsWith(".key") || n.EndsWith(".pfx") || n.EndsWith(".p12")
            || n.EndsWith(".lock") || n.EndsWith(".log") || n.EndsWith(".tmp") || n.EndsWith(".exe") || n.EndsWith(".dll")
            || n.EndsWith("-wal") || n.EndsWith("-shm") || n.EndsWith("-journal");
    }

    /// <summary>
    /// Session transcripts, prompt history, logs and per-session runtime state inside a CLI home (native or a
    /// VibeRails environment). They are not configuration, hold most of a home's bytes and change whenever a CLI
    /// runs, so a configuration fingerprint containing them never settles. Directory-level memory and
    /// instructions (<c>~/.claude/projects/*/memory</c>, skills, commands, agents) are kept.
    /// </summary>
    internal static bool IsCliRuntimeState(string name, bool directory)
    {
        var n = name.ToLowerInvariant();
        if (directory)
            return n is "sessions" or "archived_sessions" or "session-state" or "history-session-state" or "session-env"
                    or "file-history" or "shell-snapshots" or "shell_snapshots" or "debug" or "todos" or "statsig"
                    or "packages" or "vendor" or "vendor_imports"
                // Per-session directories (Claude subagent transcripts and tool results, task state).
                || Guid.TryParse(n, out _);
        return n.EndsWith(".jsonl") || (IsDatabase(n) && (n.StartsWith("logs") || n.Contains("history")));
    }

    /// <summary>True when <paramref name="word"/> is a whole <c>.</c>/<c>-</c>/<c>_</c>/space delimited segment of the lower-cased name.</summary>
    private static bool HasSegment(string lowerName, string word)
    {
        foreach (var segment in lowerName.Split(SegmentSeparators, StringSplitOptions.RemoveEmptyEntries))
            if (segment == word) return true;
        return false;
    }

    private static readonly char[] SegmentSeparators = ['.', '-', '_', ' '];

    /// <summary>
    /// Every captured JSON file is rewritten through <see cref="WriteSettings"/> rather than copied: MCP servers,
    /// provider keys and headers live in settings, <c>mcp_config.json</c>, <c>.mcp.json</c>, plugin manifests and
    /// <c>opencode.json(c)</c> alike, so a name list would always miss the next variant.
    /// </summary>
    internal static bool IsRedactedJson(string name) => Path.GetExtension(name).ToLowerInvariant() is ".json" or ".jsonc";

    /// <summary>TOML configuration (Codex and Grok <c>config.toml</c> define MCP servers) is rewritten through <see cref="RedactToml"/>.</summary>
    internal static bool IsRedactedToml(string name) => Path.GetExtension(name).ToLowerInvariant() is ".toml";

    private static bool IsDatabase(string path) => Path.GetExtension(path).ToLowerInvariant() is ".db" or ".sqlite" or ".sqlite3";

    internal static string FileStamp(string path)
    {
        var f = new FileInfo(path);
        return f.Exists ? $"{f.Length}:{f.LastWriteTimeUtc.Ticks}" : "missing";
    }

    // Only a database has a WAL whose writes leave the main file's stamp unchanged; other files need one stat.
    internal static string Fingerprint(IEnumerable<BackupFileSource> files) => BackupFormat.Hash(Encoding.UTF8.GetBytes(
        string.Join('\n', files.Select(f => $"{f.Entry}|{f.Path}|{f.Issue}|{FileStamp(f.Path)}"
            + (IsDatabase(f.Path) ? $"|{FileStamp(f.Path + "-wal")}" : "")))));

    internal static async Task<List<string>> WriteZipAsync(Stream output, IReadOnlyList<BackupFileSource> sources,
        IDatabaseSnapshotStore snapshots, string work, CancellationToken ct)
    {
        var issues = new List<string>();
        var inventory = new List<BackupFileEntry>();
        var linkedDirectories = new Dictionary<string, bool>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var buffer = new byte[128 * 1024];
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            if (source.Issue is not null) { Issue(source.Issue); continue; }
            Stream content;
            string? temporary;
            try
            {
                if (HasLink(source.Path, linkedDirectories)) { Issue("File became a link."); continue; }
                var stamp = FileStamp(source.Path);
                (content, temporary) = await CaptureAsync(source.Path, snapshots, work, ct);
                // A database snapshot is consistent by construction; any other file must be unchanged by its read.
                if (!IsDatabase(source.Path) && stamp != FileStamp(source.Path)) Issue("File changed during capture; retry required.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or StorageException)
            { Issue("File could not be completely captured."); continue; }
            // The entry exists only once the source has been read completely, so a read or parse failure never
            // leaves an empty or partial entry behind. A failure writing the archive itself aborts the version.
            try
            {
                await using (content)
                {
                    await using var entry = zip.CreateEntry(source.Entry, CompressionLevel.Fastest).Open();
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    long bytes = 0;
                    int read;
                    while ((read = await content.ReadAsync(buffer, ct)) > 0)
                    { await entry.WriteAsync(buffer.AsMemory(0, read), ct); hash.AppendData(buffer, 0, read); bytes += read; }
                    inventory.Add(new(source.Entry, source.Path, bytes, Convert.ToHexStringLower(hash.GetHashAndReset())));
                }
            }
            finally { if (temporary is not null) File.Delete(temporary); }

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

    /// <summary>
    /// Reads one source completely: databases as online snapshots, redacted JSON and small files in memory,
    /// anything larger copied into the private version directory. Returns the readable copy and any staging file.
    /// </summary>
    private static async Task<(Stream Content, string? Temporary)> CaptureAsync(string path, IDatabaseSnapshotStore snapshots, string work, CancellationToken ct)
    {
        string? temporary = null;
        try
        {
            if (IsDatabase(path))
            {
                temporary = Path.Combine(work, "native-" + Guid.NewGuid().ToString("N") + ".db");
                await snapshots.CreateSnapshotAsync(path, temporary, ct);
                return (OpenRead(temporary), temporary);
            }
            await using var input = OpenRead(path);
            if (IsRedactedJson(Path.GetFileName(path)))
            {
                using var doc = await JsonDocument.ParseAsync(input, RedactedJsonOptions, ct);
                var redacted = new MemoryStream();
                using (var writer = new Utf8JsonWriter(redacted)) WriteSettings(doc.RootElement, writer);
                redacted.Position = 0;
                return (redacted, null);
            }
            if (IsRedactedToml(Path.GetFileName(path)))
            {
                using var reader = new StreamReader(input, Encoding.UTF8);
                return (new MemoryStream(Encoding.UTF8.GetBytes(RedactToml(await reader.ReadToEndAsync(ct)))), null);
            }
            if (input.Length <= InMemoryCaptureBytes)
            {
                var copy = new MemoryStream((int)input.Length);
                await input.CopyToAsync(copy, ct);
                copy.Position = 0;
                return (copy, null);
            }
            temporary = Path.Combine(work, "copy-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var staged = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous))
                await input.CopyToAsync(staged, ct);
            return (OpenRead(temporary), temporary);
        }
        catch (Exception)
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            throw;
        }
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
        128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    // opencode.jsonc and hand-edited settings carry comments and trailing commas; a parse failure would drop the whole file.
    private static readonly JsonDocumentOptions RedactedJsonOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static void WriteSettings(JsonElement value, Utf8JsonWriter writer)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var p in value.EnumerateObject())
            {
                var name = NormalizeName(p.Name);
                if (IsCredentialProperty(name)) continue;
                writer.WritePropertyName(p.Name);
                // Environment and header blocks (MCP servers, Claude `env`) hold credentials under arbitrary names
                // (X-Auth, Cookie, DATABASE_URL): keep the block so the configuration stays valid, drop every entry.
                if (IsBlockName(name) && p.Value.ValueKind == JsonValueKind.Object)
                { writer.WriteStartObject(); writer.WriteEndObject(); }
                else WriteSettings(p.Value, writer);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String))
        {
            // String lists (args, OpenCode's command array) may split a credential flag from its value.
            writer.WriteStartArray();
            foreach (var item in RedactArguments(value.EnumerateArray().Select(item => item.GetString()!).ToList())) writer.WriteStringValue(item);
            writer.WriteEndArray();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        { writer.WriteStartArray(); foreach (var item in value.EnumerateArray()) WriteSettings(item, writer); writer.WriteEndArray(); }
        else if (value.ValueKind == JsonValueKind.String) writer.WriteStringValue(RedactText(value.GetString()!));
        else value.WriteTo(writer);
    }

    /// <summary>A property name (lower-cased, without <c>_</c>/<c>-</c>) that names a credential wherever it appears.</summary>
    internal static bool IsCredentialProperty(string name) =>
        name.Contains("apikey") || name.Contains("password") || name.Contains("passwd") || name.Contains("secret")
        || name.Contains("credential") || name.Contains("privatekey") || name.Contains("accesskey") || name.Contains("cookie")
        || name.Contains("authorization") || name.Contains("bearer") || name.Contains("sessionid") || name.Contains("sessionkey")
        || name.EndsWith("token") || name.EndsWith("auth")
        // OPENAI_KEY, STRIPE_SECRET_KEY, GITHUB_PAT; a bare "key" is a keystroke in keybinding files.
        || (name.Length > 3 && (name.EndsWith("key") || name.EndsWith("pat")))
        || name is "tokens" or "pinhash" or "pinsalt" or "remoteaccountemail" or "remoteaccountkeyfingerprint";

    private static string NormalizeName(string name) => name.Replace("_", "").Replace("-", "").ToLowerInvariant();
    private static bool IsBlockName(string normalized) => normalized is "env" or "environment" or "headers" or "httpheaders";

    /// <summary>Stands in for a credential inside a string or list, where dropping it would change the shape around it.</summary>
    internal const string RedactedValue = "[redacted]";

    /// <summary>
    /// A string list (MCP <c>args</c>, a command array): each item through <see cref="RedactText"/>, plus the item
    /// after a credential-named flag (<c>"--api-key", "sk-…"</c>) or an auth scheme (<c>"Bearer", "…"</c>), and the
    /// value carried by a header/environment flag (<c>"--header", "X-Deployment: …"</c>, <c>"-e", "NAME=…"</c>).
    /// </summary>
    internal static List<string> RedactArguments(IReadOnlyList<string> items)
    {
        var result = new List<string>(items.Count);
        var next = NextWord.Ordinary;
        foreach (var item in items)
        {
            if (next != NextWord.Ordinary && !item.StartsWith('-')) { result.Add(RedactCarried(item, next)); next = NextWord.Ordinary; continue; }
            next = NextWordAfter(item.Trim());
            result.Add(RedactText(item));
        }
        return result;
    }

    /// <summary>What the word after a flag or scheme is: ordinary, a hidden credential, a header, or an environment assignment.</summary>
    private enum NextWord { Ordinary, Hidden, Header, Environment }

    private static NextWord NextWordAfter(string word)
    {
        var (kind, valueOffset) = Carrier(word);
        if (kind != NextWord.Ordinary) return valueOffset < 0 ? kind : NextWord.Ordinary;
        return HidesNextWord(word) ? NextWord.Hidden : NextWord.Ordinary;
    }

    /// <summary>
    /// A word that is a carrier flag: alone (value offset -1; the next word is the value) or with its value attached,
    /// as <c>--header=Name: …</c>, <c>--env=NAME=…</c> or the short forms <c>-HName: …</c> and <c>-eNAME=…</c>.
    /// </summary>
    private static (NextWord Kind, int ValueOffset) Carrier(string word)
    {
        if (word.Length > 2 && word[0] == '-' && word[1] != '-' && CarrierKind(word[..2]) is var attached && attached != NextWord.Ordinary) return (attached, 2);
        var equals = word.IndexOf('=');
        var kind = CarrierKind(equals < 0 ? word : word[..equals]);
        return kind == NextWord.Ordinary ? (NextWord.Ordinary, -1) : (kind, equals < 0 ? -1 : equals + 1);
    }

    /// <summary>
    /// A flag whose value is a header (<c>--header</c>, <c>-H</c>) or an environment assignment (<c>--env</c>, <c>-e</c>).
    /// As in the <c>headers</c>/<c>env</c> blocks, the name inside is arbitrary and the value is a credential.
    /// </summary>
    private static NextWord CarrierKind(string flag)
    {
        if (flag == "-H") return NextWord.Header;
        if (flag == "-e") return NextWord.Environment;
        if (!flag.StartsWith("--", StringComparison.Ordinal)) return NextWord.Ordinary;
        return NormalizeName(flag[2..]) switch
        {
            "header" or "headers" or "httpheader" or "httpheaders" => NextWord.Header,
            "env" or "environment" => NextWord.Environment,
            _ => NextWord.Ordinary,
        };
    }

    /// <summary>
    /// A carried value keeps its name and loses its value (<c>X-Deployment:[redacted]</c>, <c>NAME=[redacted]</c>), so
    /// a restore shows what to re-enter. A header without a name is replaced whole; an environment name without a
    /// value (<c>-e NAME</c>, passed through from the host) carries nothing.
    /// </summary>
    private static string RedactCarried(string text, NextWord kind)
    {
        if (kind == NextWord.Hidden) return RedactedValue;
        var separator = text.IndexOf(kind == NextWord.Header ? ':' : '=');
        if (separator >= 0) return text[..(separator + 1)] + RedactedValue;
        return kind == NextWord.Header ? RedactedValue : RedactText(text);
    }

    /// <summary>
    /// Every JSON/TOML string value: URL passwords; the value of any <c>name=value</c> or <c>Name: value</c> pair whose
    /// name is a credential, however deeply nested (<c>--header=Authorization: Bearer …</c>, <c>--env=API_KEY=…</c>,
    /// <c>…/sse?a=1&amp;token=…</c>); the value carried by a header/environment flag whatever its name
    /// (<c>--header X-Deployment: …</c>, <c>--env=NAME=…</c>); and inside space-separated text, the word after a
    /// credential flag or auth scheme (<c>server --api-key sk-…</c>). Everything after a credential separator is
    /// replaced, never partially kept.
    /// </summary>
    internal static string RedactText(string value)
    {
        var text = RedactPlainText(value);
        // A percent-encoded pair (api_key%3D…) is invisible to the scan; if decoding reveals one, none of the value is kept.
        if (text.Contains('%'))
        {
            var decoded = Uri.UnescapeDataString(text);
            if (decoded != text && RedactPlainText(decoded) != decoded) return RedactedValue;
        }
        return text;
    }

    private static string RedactPlainText(string value)
    {
        var text = UrlPassword().Replace(value, RedactedValue);
        var cut = Math.Min(AssignmentCut(text), CarrierCut(text));
        if (cut < text.Length) text = text[..cut] + RedactedValue;
        var words = WordSeparators().Split(text); // separators are kept at odd indexes
        if (words.Length == 1) return text;
        for (int i = 0, hidden = 0; i < words.Length; i += 2)
        {
            if (words[i].Length == 0) continue;
            if (hidden == 1 && !words[i].StartsWith('-')) { words[i] = RedactedValue; hidden = 0; continue; }
            hidden = HidesNextWord(words[i]) ? 1 : 0;
        }
        return string.Concat(words);
    }

    /// <summary>Index just past the first <c>=</c>/<c>:</c> whose name is a credential, or the text's length.</summary>
    private static int AssignmentCut(string text)
    {
        for (var start = 0; ;)
        {
            var separator = text.IndexOfAny(AssignmentSeparators, start);
            if (separator < 0) return text.Length;
            var segment = text[start..separator];
            if (IsCredentialName(segment[(segment.LastIndexOfAny(WhitespaceCharacters) + 1)..])) return separator + 1;
            start = separator + 1;
        }
    }

    /// <summary>
    /// Index where the value carried by a header/environment flag begins (<c>--header X-Deployment: …</c>,
    /// <c>-e NAME=…</c>, <c>--header=Name: …</c>, <c>-HName: …</c>), or the text's length. The name's separator is looked for in the
    /// value's first word: a header without one is a value from its first character, a bare environment name is kept.
    /// </summary>
    private static int CarrierCut(string text)
    {
        var next = NextWord.Ordinary;
        var position = 0;
        foreach (var word in WordSeparators().Split(text))
        {
            var start = position;
            position += word.Length;
            if (word.Length == 0 || char.IsWhiteSpace(word[0])) continue;
            if (next != NextWord.Ordinary && !word.StartsWith('-'))
            {
                var cut = CarriedCut(text, start, word.Length, next);
                if (cut >= 0) return cut;
                next = NextWord.Ordinary;
                continue;
            }
            var (kind, valueOffset) = Carrier(word);
            if (kind == NextWord.Ordinary) { next = NextWord.Ordinary; continue; }
            if (valueOffset < 0) { next = kind; continue; }
            next = NextWord.Ordinary;
            var attached = CarriedCut(text, start + valueOffset, word.Length - valueOffset, kind);
            if (attached >= 0) return attached;
        }
        return text.Length;
    }

    /// <summary>Where a carried value starts within its word: after the name's <c>:</c>/<c>=</c>, at the word for a nameless header, -1 for a bare environment name.</summary>
    private static int CarriedCut(string text, int start, int length, NextWord kind)
    {
        var separator = text.IndexOf(kind == NextWord.Header ? ':' : '=', start, length);
        if (separator >= 0) return separator + 1;
        return kind == NextWord.Header ? start : -1;
    }

    private static bool HidesNextWord(string word) =>
        (word.StartsWith('-') && !word.Contains('=') && IsCredentialName(word))
        || word.Equals("bearer", StringComparison.OrdinalIgnoreCase) || word.Equals("basic", StringComparison.OrdinalIgnoreCase);

    /// <summary>A flag, query or assignment name that is a credential as written or once percent-decoded (<c>api%5Fkey</c>).</summary>
    private static bool IsCredentialName(string name)
    {
        var bare = name.TrimStart('-');
        if (bare.Length == 0) return false;
        if (IsCredentialProperty(NormalizeName(bare))) return true;
        return bare.Contains('%') && IsCredentialProperty(NormalizeName(Uri.UnescapeDataString(bare).TrimStart('-')));
    }

    private static readonly char[] AssignmentSeparators = ['=', ':'];
    private static readonly char[] WhitespaceCharacters = [' ', '\t', '\r', '\n'];

    [GeneratedRegex(@"(?<=://[^/@\s:]+:)[^/@\s]+(?=@)")]
    private static partial Regex UrlPassword();

    [GeneratedRegex(@"(\s+)")]
    private static partial Regex WordSeparators();

    /// <summary>
    /// Line-oriented TOML redaction with the JSON rules: comments and credential-named keys are dropped, an
    /// env/headers table keeps its header but no entries, inline env/headers tables become <c>{}</c>, string
    /// values go through <see cref="RedactText"/> and string arrays through <see cref="RedactArguments"/>.
    /// Multi-line strings and arrays are read as one value.
    /// </summary>
    internal static string RedactToml(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var output = new StringBuilder(text.Length);
        var blockedTable = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) { output.Append('\n'); continue; }
            if (line[0] == '#') continue;
            if (line[0] == '[')
            {
                var array = line.StartsWith("[[", StringComparison.Ordinal);
                var header = line[(array ? 2 : 1)..];
                var close = IndexOutsideQuotes(header, ']');
                header = close < 0 ? header : header[..close];
                blockedTable = TomlKeySegments(header).Any(s => IsBlockName(s) || IsCredentialProperty(s));
                output.Append(array ? "[[" : "[").Append(header).Append(array ? "]]" : "]").Append('\n');
                continue;
            }
            var equals = IndexOutsideQuotes(line, '=');
            var value = new StringBuilder(equals < 0 ? "" : line[(equals + 1)..]);
            while (TomlValueIsOpen(value.ToString()) && i + 1 < lines.Length) value.Append('\n').Append(lines[++i]);
            // Anything that is not a key/value pair is dropped rather than copied unexamined.
            if (equals < 0 || blockedTable) continue;
            if (RedactTomlEntry(line[..equals].Trim(), value.ToString().Trim()) is { } entry) output.Append(entry).Append('\n');
        }
        return output.ToString();
    }

    /// <summary>One redacted <c>key = value</c> pair, or null when the pair itself is a credential.</summary>
    private static string? RedactTomlEntry(string key, string value)
    {
        var segments = TomlKeySegments(key);
        var last = segments[^1];
        if (IsCredentialProperty(last) || segments.Take(segments.Count - 1).Any(IsBlockName)) return null;
        if (IsBlockName(last) && value.StartsWith('{')) return $"{key} = {{}}";
        return $"{key} = {RedactTomlValue(value)}";
    }

    /// <summary>A TOML value without its trailing comment: tables and arrays item by item, strings through <see cref="RedactText"/>.</summary>
    private static string RedactTomlValue(string value)
    {
        if (value.Length == 0) return value;
        if (value.StartsWith('{')) return RedactTomlInlineTable(value);
        if (value.StartsWith('[')) return RedactTomlArray(value);
        if (value[0] is '"' or '\'')
        {
            // The string ends where TOML ends it and whatever follows (a comment) is dropped. An unterminated
            // string cannot be examined, so none of it is kept.
            var item = value[..TomlStringEnd(value, 0)];
            if (!TryReadTomlString(item, out var s))
            {
                var quote = value.StartsWith(new string(value[0], 3), StringComparison.Ordinal) ? new string(value[0], 3) : value[..1];
                return quote + RedactedValue + quote;
            }
            if (s.Decoded is null) return s.Render(RedactedValue);
            var redacted = RedactText(s.Decoded);
            return redacted == s.Decoded ? item : s.Render(redacted);
        }
        var comment = value.IndexOf('#');
        return comment < 0 ? value : value[..comment].TrimEnd();
    }

    private static string RedactTomlInlineTable(string value)
    {
        var close = MatchingClose(value);
        var entries = new List<string>();
        foreach (var pair in SplitTopLevel(value[1..close], ','))
        {
            var equals = IndexOutsideQuotes(pair, '=');
            if (equals >= 0 && RedactTomlEntry(pair[..equals].Trim(), pair[(equals + 1)..].Trim()) is { } entry) entries.Add(entry);
        }
        return entries.Count == 0 ? "{}" : "{ " + string.Join(", ", entries) + " }";
    }

    private static string RedactTomlArray(string value)
    {
        var items = SplitTopLevel(value[1..MatchingClose(value)], ',').Select(item => item.Trim()).Where(item => item.Length > 0).ToList();
        // Every string item, however it is quoted, belongs to the argument sequence and is compared decoded: a flag and
        // its value may be written as "--api-key" and """…""". Any other item is redacted on its own.
        var strings = items.Select(item => TryReadTomlString(item, out var s) ? s : (TomlString?)null).ToList();
        var decoded = strings.Where(s => s.HasValue).Select(s => s!.Value.Decoded ?? RedactedValue).ToList();
        var arguments = RedactArguments(decoded);
        var rendered = new List<string>(items.Count);
        var next = 0;
        for (var i = 0; i < items.Count; i++)
        {
            if (strings[i] is not { } s) { rendered.Add(RedactTomlValue(items[i])); continue; }
            var (original, redacted) = (decoded[next], arguments[next++]);
            rendered.Add(redacted == original && s.Decoded is not null ? items[i] : s.Render(redacted));
        }
        return "[" + string.Join(", ", rendered) + "]";
    }

    /// <summary>
    /// One TOML string as the parser reads it: its delimiter, the newline TOML trims after an opening multi-line
    /// delimiter, and the decoded body (null when a basic-string escape is invalid, so the body cannot be examined).
    /// </summary>
    private readonly record struct TomlString(string Quote, string Lead, string? Decoded)
    {
        /// <summary>Writes <paramref name="text"/> in this string's style; a literal delimiter that cannot hold it becomes a basic one.</summary>
        public string Render(string text)
        {
            var multiLine = Quote.Length == 3;
            if (Quote[0] == '\'' && !text.Contains(Quote) && (multiLine || !text.Contains('\n'))) return Quote + Lead + text + Quote;
            var basic = new string('"', Quote.Length);
            return basic + Lead + TomlEscape(text, multiLine) + basic;
        }
    }

    /// <summary>
    /// Reads <paramref name="item"/> as exactly one complete TOML string: basic, literal or multi-line. Escapes are
    /// decoded before anything is classified, so an escaped spelling of a header table or a flag means what it decodes to.
    /// </summary>
    private static bool TryReadTomlString(string item, out TomlString s)
    {
        s = default;
        if (item.Length < 2 || item[0] is not ('"' or '\'')) return false;
        var quote = item.StartsWith(new string(item[0], 3), StringComparison.Ordinal) ? new string(item[0], 3) : item[..1];
        if (item.Length < quote.Length * 2 || TomlStringEnd(item, 0) != item.Length || !item.EndsWith(quote, StringComparison.Ordinal)) return false;
        var raw = item[quote.Length..^quote.Length];
        var lead = quote.Length == 3 && raw.StartsWith('\n') ? "\n" : "";
        raw = raw[lead.Length..];
        s = new(quote, lead, quote[0] == '\'' ? raw : TomlUnescape(raw, multiLine: quote.Length == 3));
        return true;
    }

    /// <summary>Decodes a TOML basic-string body: escapes, and line-ending backslashes in multi-line strings. Null when an escape is invalid.</summary>
    private static string? TomlUnescape(string raw, bool multiLine)
    {
        if (!raw.Contains('\\')) return raw;
        var output = new StringBuilder(raw.Length);
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '\\') { output.Append(raw[i]); continue; }
            if (++i >= raw.Length) return null;
            switch (raw[i])
            {
                case 'b': output.Append('\b'); break;
                case 't': output.Append('\t'); break;
                case 'n': output.Append('\n'); break;
                case 'f': output.Append('\f'); break;
                case 'r': output.Append('\r'); break;
                case 'e': output.Append('\u001b'); break;
                case '"': output.Append('"'); break;
                case '\\': output.Append('\\'); break;
                case 'x' or 'u' or 'U':
                    var digits = raw[i] == 'x' ? 2 : raw[i] == 'u' ? 4 : 8;
                    if (i + digits >= raw.Length || !int.TryParse(raw.AsSpan(i + 1, digits), System.Globalization.NumberStyles.AllowHexSpecifier,
                        System.Globalization.CultureInfo.InvariantCulture, out var code) || code is (>= 0xD800 and <= 0xDFFF) or > 0x10FFFF or < 0) return null;
                    output.Append(char.ConvertFromUtf32(code));
                    i += digits;
                    break;
                case ' ' or '\t' or '\r' or '\n' when multiLine:
                    // A line-ending backslash trims all whitespace and newlines up to the next content.
                    var next = i;
                    while (next < raw.Length && raw[next] is ' ' or '\t') next++;
                    if (next < raw.Length && raw[next] is not ('\r' or '\n')) return null;
                    while (next < raw.Length && raw[next] is ' ' or '\t' or '\r' or '\n') next++;
                    i = next - 1;
                    break;
                default: return null;
            }
        }
        return output.ToString();
    }

    private static string TomlEscape(string value, bool multiLine)
    {
        var output = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '\\' or '"') output.Append('\\').Append(c);
            else if (c == '\t' || (c == '\n' && multiLine)) output.Append(c);
            else if (c < ' ' || c == '\u007f') output.Append("\\u").Append(((int)c).ToString("X4", System.Globalization.CultureInfo.InvariantCulture));
            else output.Append(c);
        }
        return output.ToString();
    }

    private static List<string> TomlKeySegments(string dotted) =>
        SplitTopLevel(dotted, '.').Select(segment => NormalizeName(TomlKeyText(segment.Trim()))).ToList();

    /// <summary>A key segment as TOML reads it: quoted keys unquoted and, for basic strings, unescaped.</summary>
    private static string TomlKeyText(string segment) =>
        segment.Length >= 2 && segment[0] == '"' && segment[^1] == '"' ? TomlUnescape(segment[1..^1], multiLine: false) ?? segment
        : segment.Length >= 2 && segment[0] == '\'' && segment[^1] == '\'' ? segment[1..^1] : segment;

    /// <summary>Splits on <paramref name="separator"/> outside strings and nested brackets, dropping comments.</summary>
    private static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'')
            {
                var end = TomlStringEnd(text, i);
                current.Append(text, i, end - i);
                i = end - 1;
                continue;
            }
            if (c == '#') { while (i + 1 < text.Length && text[i + 1] != '\n') i++; continue; }
            if (c is '[' or '{') depth++;
            else if (c is ']' or '}') depth--;
            if (c == separator && depth == 0) { parts.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }
        parts.Add(current.ToString());
        return parts;
    }

    /// <summary>Index of the bracket closing the one at index 0, or the text's end when it is unterminated.</summary>
    private static int MatchingClose(string text)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c is '"' or '\'') { i = TomlStringEnd(text, i) - 1; continue; }
            if (c == '#') { while (i + 1 < text.Length && text[i + 1] != '\n') i++; continue; }
            if (c is '[' or '{') depth++;
            else if (c is ']' or '}' && --depth == 0) return i;
        }
        return text.Length;
    }

    private static int IndexOutsideQuotes(string text, char target)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '"' or '\'') { i = TomlStringEnd(text, i) - 1; continue; }
            if (text[i] == target) return i;
        }
        return -1;
    }

    /// <summary>True while a value still needs following lines: an unclosed multi-line string or bracket.</summary>
    private static bool TomlValueIsOpen(string value)
    {
        var depth = 0;
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c is '"' or '\'')
            {
                var triple = new string(c, 3);
                if (string.CompareOrdinal(value, i, triple, 0, 3) == 0 && value.IndexOf(triple, i + 3, StringComparison.Ordinal) < 0) return true;
                i = TomlStringEnd(value, i) - 1;
            }
            else if (c == '#') { var newline = value.IndexOf('\n', i); if (newline < 0) break; i = newline; }
            else if (c is '[' or '{') depth++;
            else if (c is ']' or '}') depth--;
        }
        return depth > 0;
    }

    /// <summary>Index just past the TOML string starting at <paramref name="start"/>: basic, literal or multi-line.</summary>
    private static int TomlStringEnd(string text, int start)
    {
        var quote = text[start];
        var triple = new string(quote, 3);
        if (string.CompareOrdinal(text, start, triple, 0, 3) == 0)
        {
            var close = text.IndexOf(triple, start + 3, StringComparison.Ordinal);
            if (close < 0) return text.Length;
            close += 3;
            // Up to two delimiter characters directly before the closing delimiter belong to the string ("""two""""").
            for (var extra = 0; extra < 2 && close < text.Length && text[close] == quote; extra++) close++;
            return close;
        }
        for (var i = start + 1; i < text.Length; i++)
        {
            if (quote == '"' && text[i] == '\\') { i++; continue; }
            if (text[i] == quote || text[i] == '\n') return i + 1;
        }
        return text.Length;
    }
}

public sealed record BackupFileSource(string Path, string Entry, string? Issue = null);
public sealed record BackupFileEntry(string Entry, string OriginalPath, long Bytes, string Sha256);
[System.Text.Json.Serialization.JsonSerializable(typeof(List<BackupFileEntry>))]
internal partial class BackupFilesJson : System.Text.Json.Serialization.JsonSerializerContext;
