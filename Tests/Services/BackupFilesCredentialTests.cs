using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using VibeRails.Data.Sqlite;
using VibeRails.DTOs;
using VibeRails.Services.Backups;
using Xunit;

namespace Tests.Services;

/// <summary>
/// Regression for the 2026-10-02 security finding: native CLI credential stores (OpenCode <c>mcp-auth.json</c>,
/// the Antigravity Chromium profile, Gemini account files) entered the automatic configuration archive verbatim.
/// These tests run the real enumeration and zip writer against a fixture home, not just the name predicate.
/// </summary>
public sealed class BackupFilesCredentialTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vb-backup-files-" + Guid.NewGuid().ToString("N"));
    private string Home => Path.Combine(root, "home");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public BackupFilesCredentialTests() { Directory.CreateDirectory(root); Directory.CreateDirectory(Home); }

    [Theory]
    // Every credential store observed in real CLI homes: name-based, so the family is caught, not one spelling.
    [InlineData("mcp-auth.json")]
    [InlineData("auth.json")]
    [InlineData("auth.json.lock")]
    [InlineData(".credentials.json")]
    [InlineData("oauth_creds.json")]
    [InlineData("mcp-oauth-tokens.json")]
    [InlineData("mcp-oauth-locks")]
    [InlineData("google_accounts.json")]
    [InlineData("accounts.json")]
    [InlineData(".sandbox-secrets")]
    [InlineData(".claude.json")]
    [InlineData("script_signing.json")]
    [InlineData("MCP-AUTH.JSON")]
    [InlineData(".auth")]
    // Chromium profile state: the directory and its credential files.
    [InlineData("antigravity-browser-profile")]
    [InlineData("browser_recordings")]
    [InlineData("chrome")]
    [InlineData("Cookies")]
    [InlineData("Login Data")]
    [InlineData("Web Data")]
    [InlineData("Local State")]
    // Derived caches regardless of spelling.
    [InlineData("marketplace-cache")]
    [InlineData("models_cache.json")]
    [InlineData("GPUCache")]
    [InlineData(".tmp")]
    [InlineData("projects.json.d3e0f823-9bf3-4251-bef6-6b540fd928af.tmp")]
    public void CredentialAndDerivedNamesAreExcluded(string name) => Assert.True(BackupFiles.Excluded(name));

    [Theory]
    [InlineData("author.md")]
    [InlineData("authorize_pr.py")]
    [InlineData("authentication-notes.md")]
    [InlineData("settings.json")]
    [InlineData("config.toml")]
    [InlineData("mcp_config.json")]
    [InlineData("history.jsonl")]
    [InlineData("opencode.jsonc")]
    [InlineData("session-store.db")]
    public void OrdinaryConfigurationAndHistoryNamesAreKept(string name) => Assert.False(BackupFiles.Excluded(name));

    [Theory]
    [InlineData("settings.json", true)]
    [InlineData("config.json", true)]
    [InlineData("user-settings.json", true)]
    [InlineData("mcp_config.json", true)]
    [InlineData("MCP-Config.JSON", true)]
    [InlineData("opencode.json", true)]
    [InlineData("opencode.jsonc", true)]
    // Every JSON file: MCP servers also live in .mcp.json and plugin manifests.
    [InlineData(".mcp.json", true)]
    [InlineData("plugin.json", true)]
    [InlineData("projects.json", true)]
    [InlineData("history.jsonl", false)]
    [InlineData("config.toml", false)]
    public void RedactionCoversEveryJsonFile(string name, bool redacted) => Assert.Equal(redacted, BackupFiles.IsRedactedJson(name));

    [Fact]
    public async Task EnumerationNeverListsNativeCredentialStoresOrBrowserProfiles()
    {
        var opencode = Path.Combine(Home, ".local", "share", "opencode");
        Write(Path.Combine(opencode, "mcp-auth.json"), "{\"server\":{\"accessToken\":\"leak\"}}");
        Write(Path.Combine(opencode, "auth.json"), "{\"anthropic\":{\"key\":\"leak\"}}");
        Write(Path.Combine(opencode, "plans", "todo.md"), "keep");
        Write(Path.Combine(Home, ".codex", "auth.json"), "leak");
        Write(Path.Combine(Home, ".codex", "config.toml"), "model = \"gpt\"");
        Write(Path.Combine(Home, ".codex", "mcp-oauth-locks", "file-store.lock"), "");
        Write(Path.Combine(Home, ".codex", ".sandbox-secrets", "x"), "leak");
        Write(Path.Combine(Home, ".claude", ".credentials.json"), "leak");
        Write(Path.Combine(Home, ".claude", "settings.json"), "{}");
        Write(Path.Combine(Home, ".claude", "chrome", "chrome-native-host.bat"), "");
        Write(Path.Combine(Home, ".claude.json"), "leak");
        Write(Path.Combine(Home, ".grok", "auth.json"), "leak");
        Write(Path.Combine(Home, ".grok", "trusted_folders.toml"), "keep");
        Write(Path.Combine(Home, ".gemini", "oauth_creds.json"), "leak");
        Write(Path.Combine(Home, ".gemini", "google_accounts.json"), "leak");
        Write(Path.Combine(Home, ".gemini", "antigravity", "mcp_config.json"), "{}");
        var profile = Path.Combine(Home, ".gemini", "antigravity-browser-profile");
        Write(Path.Combine(profile, "Local State"), "leak");
        Write(Path.Combine(profile, "Default", "Cookies"), "leak");
        Write(Path.Combine(profile, "Default", "Login Data"), "leak");
        Write(Path.Combine(profile, "Default", "Preferences"), "leak");

        var files = await Enumerate();
        var entries = files.Select(f => f.Entry).ToList();
        var paths = files.Select(f => f.Path).ToList();

        Assert.DoesNotContain(paths, p => File.Exists(p) && File.ReadAllText(p).Contains("leak"));
        Assert.DoesNotContain(entries, e => e.Contains("auth", StringComparison.OrdinalIgnoreCase) || e.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || e.Contains("secret", StringComparison.OrdinalIgnoreCase) || e.Contains("browser", StringComparison.OrdinalIgnoreCase)
            || e.Contains("accounts", StringComparison.OrdinalIgnoreCase) || e.EndsWith(".claude.json", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("native/opencode-data/plans/todo.md", entries);
        Assert.Contains("native/.codex/config.toml", entries);
        Assert.Contains("native/.claude/settings.json", entries);
        Assert.Contains("native/.grok/trusted_folders.toml", entries);
        Assert.Contains("native/.gemini/antigravity/mcp_config.json", entries);
        Assert.All(files, f => Assert.Null(f.Issue));
    }

    [Fact]
    public async Task McpAndOpenCodeConfigurationIsRedactedNotCopied()
    {
        var mcp = Path.Combine(root, "mcp_config.json");
        await File.WriteAllTextAsync(mcp, "{\"mcpServers\":{\"github\":{\"command\":\"npx\",\"env\":{\"GITHUB_PERSONAL_ACCESS_TOKEN\":\"ghp_leak\",\"NODE_ENV\":\"production\"},\"headers\":{\"Authorization\":\"Bearer leak\",\"Accept\":\"application/json\"}}}}", Ct);
        var opencode = Path.Combine(root, "opencode.jsonc");
        await File.WriteAllTextAsync(opencode, "{\n  // provider keys live beside preferences\n  \"provider\": { \"openai\": { \"options\": { \"apiKey\": \"sk-leak\", \"baseURL\": \"https://example\" } } },\n  \"credentials\": { \"x\": 1 },\n  \"theme\": \"dark\",\n}", Ct);
        var sources = new List<BackupFileSource> { new(mcp, "native/.gemini/antigravity/mcp_config.json"), new(opencode, "native/opencode-config/opencode.jsonc") };

        using var output = new MemoryStream();
        var issues = await BackupFiles.WriteZipAsync(output, sources, new SqliteDatabaseSnapshotStore(), root, Ct);
        Assert.Empty(issues);
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read);
        var mcpText = await Read(zip, sources[0].Entry);
        Assert.DoesNotContain("ghp_leak", mcpText); Assert.DoesNotContain("Bearer leak", mcpText);
        // Env and header blocks keep their shape but lose every entry: their names are arbitrary, their values credentials.
        Assert.Contains("\"env\":{}", mcpText); Assert.Contains("\"headers\":{}", mcpText); Assert.DoesNotContain("NODE_ENV", mcpText);
        Assert.Contains("npx", mcpText);
        var opencodeText = await Read(zip, sources[1].Entry);
        Assert.DoesNotContain("sk-leak", opencodeText); Assert.DoesNotContain("credentials", opencodeText);
        Assert.Contains("https://example", opencodeText); Assert.Contains("dark", opencodeText);
    }

    [Theory]
    // Credential names outside an env/headers block, after case, '_' and '-' are ignored.
    [InlineData("Proxy-Authorization")]
    [InlineData("X-Auth")]
    [InlineData("Cookie")]
    [InlineData("set_cookie")]
    [InlineData("access_key")]
    [InlineData("AWS_SECRET_ACCESS_KEY")]
    [InlineData("session_id")]
    [InlineData("passwd")]
    [InlineData("bearer")]
    [InlineData("refresh_token")]
    [InlineData("OPENAI_KEY")]
    [InlineData("GITHUB_PAT")]
    public void CommonCredentialPropertyNamesAreRedacted(string name) =>
        Assert.True(BackupFiles.IsCredentialProperty(name.Replace("_", "").Replace("-", "").ToLowerInvariant()));

    [Theory]
    [InlineData("RemoveCoAuthorTrailers")]
    [InlineData("ClaudeTokenSaverEnabled")]
    [InlineData("author")]
    [InlineData("theme")]
    [InlineData("command")]
    [InlineData("key")] // keybindings.json and similar use "key" for a keystroke
    public void OrdinaryPreferencesAreNotCredentialProperties(string name) =>
        Assert.False(BackupFiles.IsCredentialProperty(name.Replace("_", "").Replace("-", "").ToLowerInvariant()));

    [Fact]
    public async Task HeadersEnvBlocksAndCommonCredentialKeysNeverReachTheArchive()
    {
        var settings = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(settings, """
            {
              "env": { "ANTHROPIC_BASE_URL": "https://proxy-leak", "DATABASE_URL": "postgres://u:pw-leak@db" },
              "autoMode": { "environment": ["Local workstation"] },
              "RemoveCoAuthorTrailers": true,
              "mcpServers": { "remote": { "url": "https://mcp.example", "http_headers": { "X-Auth": "xauth-leak" },
                "Proxy-Authorization": "proxy-leak", "Cookie": "cookie-leak", "access_key": "ak-leak", "session_id": "sid-leak" } }
            }
            """, Ct);
        var sources = new List<BackupFileSource> { new(settings, "native/.claude/settings.json") };

        using var output = new MemoryStream();
        Assert.Empty(await BackupFiles.WriteZipAsync(output, sources, new SqliteDatabaseSnapshotStore(), root, Ct));
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read);
        var text = await Read(zip, sources[0].Entry);

        Assert.DoesNotContain("leak", text);
        Assert.Contains("\"env\":{}", text); Assert.Contains("\"http_headers\":{}", text);
        // A list-valued "environment" is ordinary configuration, not an env block.
        Assert.Contains("Local workstation", text); Assert.Contains("RemoveCoAuthorTrailers", text); Assert.Contains("https://mcp.example", text);
    }

    [Fact]
    public async Task UnreadableJsonLeavesNoEntryInsteadOfAnEmptyOne()
    {
        var broken = Path.Combine(root, "settings.json");
        await File.WriteAllTextAsync(broken, "{ \"theme\": \"dark\", ", Ct);
        var kept = Path.Combine(root, "notes.md");
        await File.WriteAllTextAsync(kept, "kept", Ct);
        var sources = new List<BackupFileSource> { new(broken, "native/.claude/settings.json"), new(kept, "native/.claude/notes.md") };

        using var output = new MemoryStream();
        var issues = await BackupFiles.WriteZipAsync(output, sources, new SqliteDatabaseSnapshotStore(), root, Ct);
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read);

        Assert.Equal("native/.claude/settings.json: File could not be completely captured.", Assert.Single(issues));
        // A restore must never find a zero-byte settings.json to write over the real one.
        Assert.Null(zip.GetEntry("native/.claude/settings.json"));
        Assert.Equal("kept", await Read(zip, "native/.claude/notes.md"));
        Assert.DoesNotContain("settings.json", await Read(zip, "files.json"));
    }

    [Fact]
    public async Task LargeFilesAreStagedAndTheirStagingCopyRemoved()
    {
        var large = Path.Combine(root, "large.bin");
        var bytes = new byte[BackupFiles.InMemoryCaptureBytes + 4096];
        new Random(7).NextBytes(bytes);
        await File.WriteAllBytesAsync(large, bytes, Ct);
        var work = Path.Combine(root, "work"); Directory.CreateDirectory(work);

        using var output = new MemoryStream();
        Assert.Empty(await BackupFiles.WriteZipAsync(output, [new(large, "native/.claude/large.bin")], new SqliteDatabaseSnapshotStore(), work, Ct));
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read);
        using var copy = new MemoryStream();
        await using (var entry = zip.GetEntry("native/.claude/large.bin")!.Open()) await entry.CopyToAsync(copy, Ct);

        Assert.Equal(bytes, copy.ToArray());
        Assert.Empty(Directory.GetFiles(work));
    }

    [Fact]
    public async Task CliHomesKeepConfigurationAndMemoryButNotTranscriptsHistoryOrLogStores()
    {
        var project = Path.Combine(Home, ".claude", "projects", "C--source-app");
        var session = Guid.NewGuid().ToString();
        Write(Path.Combine(project, session + ".jsonl"), "transcript");
        Write(Path.Combine(project, session, "subagents", "agent-1.jsonl"), "transcript");
        Write(Path.Combine(project, session, "tool-results", "result.txt"), "transcript");
        Write(Path.Combine(project, "memory", "MEMORY.md"), "keep");
        Write(Path.Combine(Home, ".claude", "history.jsonl"), "history");
        Write(Path.Combine(Home, ".claude", "file-history", session, "a@v1"), "history");
        Write(Path.Combine(Home, ".claude", "shell-snapshots", "snapshot-bash.sh"), "runtime");
        Write(Path.Combine(Home, ".claude", "debug", "latest"), "runtime");
        Write(Path.Combine(Home, ".claude", "backups", ".claude.json.backup.1790949915055"), "account");
        Write(Path.Combine(Home, ".claude", "skills", "review", "SKILL.md"), "keep");
        Write(Path.Combine(Home, ".codex", "sessions", "2026", "10", "02", "rollout-1.jsonl"), "transcript");
        Write(Path.Combine(Home, ".codex", "archived_sessions", "rollout-0.jsonl"), "transcript");
        Write(Path.Combine(Home, ".codex", "logs_2.sqlite"), "logs");
        Write(Path.Combine(Home, ".codex", "thread_history_1.sqlite"), "transcript");
        Write(Path.Combine(Home, ".codex", "packages", "app-server", "index.js"), "download");
        Write(Path.Combine(Home, ".codex", "AGENTS.md"), "keep");
        Write(Path.Combine(Home, ".grok", "sessions", "x", "messages.jsonl"), "transcript");
        Write(Path.Combine(Home, ".copilot", "session-state", "s.json"), "runtime");
        var install = Path.Combine(root, "install");
        Write(Path.Combine(install, "envs", "Review", "codex", "sessions", "rollout-2.jsonl"), "transcript");
        Write(Path.Combine(install, "envs", "Review", "codex", "config.toml"), "keep");
        Write(Path.Combine(install, "history", "prompts.jsonl"), "keep"); // VibeRails' own history is not a CLI home.

        var entries = (await Enumerate()).Select(f => f.Entry).ToList();

        Assert.Equal([
            "installation/envs/Review/codex/config.toml",
            "installation/history/prompts.jsonl",
            "native/.claude/projects/C--source-app/memory/MEMORY.md",
            "native/.claude/skills/review/SKILL.md",
            "native/.codex/AGENTS.md"], entries);
    }

    [Fact]
    public async Task ReferencedScriptsAreCapturedWhateverTheirName_AndInvalidStoredPathsBecomeIssues()
    {
        var repository = Path.Combine(root, "repo");
        Write(Path.Combine(repository, "rotate_token.py"), "print('rotate')");
        Write(Path.Combine(repository, "cache_warm.py"), "print('warm')");
        var state = Path.Combine(root, "state.db");
        SqliteStorage.EnsureAllSchemas(new(state));
        var jobs = SqliteStorage.CreateJobStore(state);
        await jobs.CreateJobAsync(new("Rotate", repository, VibeRails.Services.LLM.NotSet, null, "", null, true, [],
            Actions: [new(null, JobActionKind.Script, ScriptPath: "rotate_token.py"),
                new(null, JobActionKind.Script, ScriptPath: "cache_warm.py"),
                new(null, JobActionKind.Script, ScriptPath: "missing.py")]), Ct);
        var legacy = await jobs.CreateJobAsync(new("Legacy", repository, VibeRails.Services.LLM.NotSet, null, "", null, true, [],
            Actions: [new(null, JobActionKind.Script, ScriptPath: "check.py")]), Ct);
        // The store absolutizes new paths; an older row can still hold a relative one, which GetFullPath(path, base) rejects.
        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={state};Pooling=False"))
        {
            db.Open();
            using var update = db.CreateCommand();
            update.CommandText = "UPDATE Jobs SET ProjectPath = 'relative/repo' WHERE Id = $id";
            update.Parameters.AddWithValue("$id", legacy.Id);
            Assert.Equal(1, update.ExecuteNonQuery());
        }

        var files = (await Enumerate()).Where(f => f.Entry.StartsWith("automation/", StringComparison.Ordinal)).ToList();

        Assert.Contains(files, f => f.Entry.EndsWith("/rotate_token.py", StringComparison.Ordinal) && f.Issue is null);
        Assert.Contains(files, f => f.Entry.EndsWith("/cache_warm.py", StringComparison.Ordinal) && f.Issue is null);
        Assert.Contains(files, f => f.Issue == "Referenced script is missing.");
        Assert.Contains(files, f => f.Issue == "Automation repository path is not absolute.");
    }

    [Theory]
    [InlineData(new[] { "-y", "@modelcontextprotocol/server-github", "--api-key", "sk-leak" }, new[] { "-y", "@modelcontextprotocol/server-github", "--api-key", BackupFiles.RedactedValue })]
    [InlineData(new[] { "--token=tok-leak", "--port=3000" }, new[] { "--token=" + BackupFiles.RedactedValue, "--port=3000" })]
    [InlineData(new[] { "mcp-remote", "https://mcp.example/sse", "--header", "Authorization: Bearer leak" }, new[] { "mcp-remote", "https://mcp.example/sse", "--header", "Authorization:" + BackupFiles.RedactedValue })]
    // A credential-named switch followed by another flag has no value to hide.
    [InlineData(new[] { "--no-auth", "--verbose" }, new[] { "--no-auth", "--verbose" })]
    // Credentials nested inside an ordinary option's value (VIBE-35 re-review).
    [InlineData(new[] { "--header=Authorization: Bearer leak" }, new[] { "--header=Authorization:" + BackupFiles.RedactedValue })]
    [InlineData(new[] { "--env=API_KEY=leak" }, new[] { "--env=API_KEY=" + BackupFiles.RedactedValue })]
    [InlineData(new[] { "run", "-e", "OPENAI_KEY=leak" }, new[] { "run", "-e", "OPENAI_KEY=" + BackupFiles.RedactedValue })]
    // Header and environment carriers (VIBE-37): the name inside is arbitrary, so the value goes whatever it is called,
    // as it does in the headers/env blocks; the name stays so a restore shows what to re-enter.
    [InlineData(new[] { "https://mcp.example/sse", "--header", "X-Deployment: leak" }, new[] { "https://mcp.example/sse", "--header", "X-Deployment:" + BackupFiles.RedactedValue })]
    [InlineData(new[] { "-H", "X-Deployment: leak", "--port", "3000" }, new[] { "-H", "X-Deployment:" + BackupFiles.RedactedValue, "--port", "3000" })]
    [InlineData(new[] { "--header=X-Deployment: leak" }, new[] { "--header=X-Deployment:" + BackupFiles.RedactedValue })]
    [InlineData(new[] { "--http-header", "X-Deployment:leak" }, new[] { "--http-header", "X-Deployment:" + BackupFiles.RedactedValue })]
    [InlineData(new[] { "run", "-e", "GITHUB_PERSONAL_ACCESS_TOKEN=ghp-leak", "-e", "LOG_LEVEL=leak" }, new[] { "run", "-e", "GITHUB_PERSONAL_ACCESS_TOKEN=" + BackupFiles.RedactedValue, "-e", "LOG_LEVEL=" + BackupFiles.RedactedValue })]
    [InlineData(new[] { "--env=REGION=leak", "--env", "DEPLOYMENT=leak" }, new[] { "--env=REGION=" + BackupFiles.RedactedValue, "--env", "DEPLOYMENT=" + BackupFiles.RedactedValue })]
    // A header without a name is all value; an environment name without a value is passed through from the host.
    [InlineData(new[] { "-H", "Authorization", "Bearer", "leak" }, new[] { "-H", BackupFiles.RedactedValue, "Bearer", BackupFiles.RedactedValue })]
    [InlineData(new[] { "run", "-e", "GITHUB_PERSONAL_ACCESS_TOKEN", "ghcr.io/github/github-mcp-server" }, new[] { "run", "-e", "GITHUB_PERSONAL_ACCESS_TOKEN", "ghcr.io/github/github-mcp-server" })]
    [InlineData(new[] { "--env-file", ".env.production", "--header-case", "lower" }, new[] { "--env-file", ".env.production", "--header-case", "lower" })]
    // Short options carry their value attached as well (curl -HName: value, docker -eNAME=value); found by the VIBE-37 review.
    [InlineData(new[] { "-HX-Deployment: leak", "-eCUSTOM=leak", "-e", "KEEP", "-Hleak" }, new[] { "-HX-Deployment:" + BackupFiles.RedactedValue, "-eCUSTOM=" + BackupFiles.RedactedValue, "-e", "KEEP", "-H" + BackupFiles.RedactedValue })]
    public void CredentialArgumentsAreReplacedInPlace(string[] args, string[] expected) => Assert.Equal(expected, BackupFiles.RedactArguments(args));

    [Theory]
    [InlineData("https://mcp.example/sse?team=core&access_token=leak", "https://mcp.example/sse?team=core&access_token=[redacted]")]
    [InlineData("https://mcp.example/sse?api_key=leak", "https://mcp.example/sse?api_key=[redacted]")]
    [InlineData("postgres://app:leak@db.example:5432/app", "postgres://app:[redacted]@db.example:5432/app")]
    [InlineData("npx deploy-mcp --api-key leak --port 3000", "npx deploy-mcp --api-key [redacted] --port 3000")]
    [InlineData("curl -H 'Authorization: Bearer leak'", "curl -H 'Authorization:[redacted]")]
    [InlineData("Bearer leak", "Bearer [redacted]")]
    // Percent-encoded query names mean what they decode to (VIBE-35 third review).
    [InlineData("https://mcp.example/sse?api%5Fkey=leak", "https://mcp.example/sse?api%5Fkey=[redacted]")]
    [InlineData("https://mcp.example/sse?api_key%3Dleak", "[redacted]")]
    [InlineData("50%25 of the budget", "50%25 of the budget")]
    // Header/environment carriers in free text (VIBE-37): the name's separator is looked for in the first value word.
    [InlineData("mcp-remote https://mcp.example/sse --header 'X-Deployment: leak'", "mcp-remote https://mcp.example/sse --header 'X-Deployment:[redacted]")]
    [InlineData("--header=X-Deployment: leak", "--header=X-Deployment:[redacted]")]
    [InlineData("docker run -e DEPLOYMENT=leak image", "docker run -e DEPLOYMENT=[redacted]")]
    [InlineData("docker run -e GITHUB_PERSONAL_ACCESS_TOKEN ghcr.io/github/github-mcp-server", "docker run -e GITHUB_PERSONAL_ACCESS_TOKEN ghcr.io/github/github-mcp-server")]
    [InlineData("node -e console.log(1)", "node -e console.log(1)")]
    [InlineData("curl -HX-Deployment:leak https://mcp.example", "curl -HX-Deployment:[redacted]")]
    [InlineData("docker run -eCUSTOM=leak image", "docker run -eCUSTOM=[redacted]")]
    [InlineData("set -ex && node server.js", "set -ex && node server.js")]
    // A quoted value after a credential flag or scheme is one value through its closing quote (VIBE-37 review).
    [InlineData("server --password \"prefix leak suffix\" --port 3000", "server --password [redacted] --port 3000")]
    [InlineData("server --api-key 'leak more' --verbose", "server --api-key [redacted] --verbose")]
    [InlineData("Bearer \"leak leak\"", "Bearer [redacted]")]
    [InlineData("server --password \"leak unterminated --port 3000", "server --password [redacted]")]
    // Ordinary values are untouched.
    [InlineData("https://mcp.example/sse", "https://mcp.example/sse")]
    [InlineData(@"C:\Users\me\.claude\hooks\check_token.py --mode=strict", @"C:\Users\me\.claude\hooks\check_token.py --mode=strict")]
    [InlineData("gh auth token", "gh auth token")]
    [InlineData("Local workstation", "Local workstation")]
    public void StringValuesLoseEmbeddedCredentials(string value, string expected) => Assert.Equal(expected, BackupFiles.RedactText(value));

    [Fact]
    public void TomlRedactionKeepsConfigurationAndDropsEveryCredentialShape()
    {
        var redacted = BackupFiles.RedactToml(""""
            # token = "comment-leak"
            model = "gpt-5"
            experimental_bearer_token = "bearer-leak"

            [mcp_servers.github]
            command = "npx"
            args = [
              "-y", # the package
              "@modelcontextprotocol/server-github",
              "--token", "args-leak",
            ]
            env = { GITHUB_TOKEN = "inline-leak" }
            env.EXTRA = "dotted-leak"

            [mcp_servers.github.env]
            GITHUB_PERSONAL_ACCESS_TOKEN = "table-leak"
            NOTES = '''
            multi-line-leak
            '''

            [mcp_servers.remote]
            url = "https://mcp.example"
            http_headers = { "X-Auth" = "xauth-leak", Accept = "application/json" }
            options = { timeout = 30, api_key = "nested-leak" }
            args = ["--header=Authorization: Bearer header-leak", "--env=API_KEY=env-leak"]
            sse = "https://mcp.example/sse?token=query-leak" # comment-leak
            command = """
            server --api-key multi-leak
            """

            [mcp_servers.synthetic."http\u005fheaders"]
            X-Deployment = "escaped-header-leak"

            [mcp_servers.synthetic]
            args = ["--api\u002dkey", "escaped-arg-leak"]
            url = "https://mcp.example/sse?api_key\u003descaped-url-leak"
            "\u0065nv" = { KEY = "escaped-env-leak" }
            notes = """
            api_\
              key=continued-leak"""

            [mcp_servers.multiline]
            args = ["--api-key", """multiline-value-leak"""]
            flags = ['''--api-key''', "multiline-flag-leak"]
            literal = ["--token", '''literal-value-leak''']
            spread = [
              "--token",
              """
            spread-leak""",
            ]
            proxy = ["https://mcp.example/sse", "--header", "X-Deployment: header-arg-leak"]
            command = "mcp-remote https://mcp.example/sse --header 'X-Deployment: text-header-leak'"
            compact = ["-HX-Deployment: compact-header-leak", "-eCUSTOM=compact-env-leak"]
            quoted = "server --password \"prefix quoted-leak suffix\" --port 3000"
            """");

        Assert.DoesNotContain("leak", redacted);
        // Multi-line and literal strings join the argument sequence whatever their quoting, and header carriers lose their value (VIBE-37).
        Assert.Contains($"args = [\"--api-key\", \"\"\"{BackupFiles.RedactedValue}\"\"\"]", redacted);
        Assert.Contains($"flags = ['''--api-key''', \"{BackupFiles.RedactedValue}\"]", redacted);
        Assert.Contains($"literal = [\"--token\", '''{BackupFiles.RedactedValue}''']", redacted);
        Assert.Contains($"spread = [\"--token\", \"\"\"\n{BackupFiles.RedactedValue}\"\"\"]", redacted);
        Assert.Contains($"proxy = [\"https://mcp.example/sse\", \"--header\", \"X-Deployment:{BackupFiles.RedactedValue}\"]", redacted);
        Assert.Contains($"command = \"mcp-remote https://mcp.example/sse --header 'X-Deployment:{BackupFiles.RedactedValue}\"", redacted);
        Assert.Contains($"compact = [\"-HX-Deployment:{BackupFiles.RedactedValue}\", \"-eCUSTOM={BackupFiles.RedactedValue}\"]", redacted);
        Assert.Contains($"quoted = \"server --password {BackupFiles.RedactedValue} --port 3000\"", redacted);
        Assert.Contains("model = \"gpt-5\"", redacted);
        Assert.Contains("[mcp_servers.github]", redacted); Assert.Contains("command = \"npx\"", redacted);
        Assert.Contains($"args = [\"-y\", \"@modelcontextprotocol/server-github\", \"--token\", \"{BackupFiles.RedactedValue}\"]", redacted);
        Assert.Contains("env = {}", redacted);
        Assert.Contains("[mcp_servers.github.env]", redacted);
        Assert.Contains("url = \"https://mcp.example\"", redacted);
        Assert.Contains("http_headers = {}", redacted);
        Assert.Contains("options = { timeout = 30 }", redacted);
        Assert.Contains($"args = [\"--header=Authorization:{BackupFiles.RedactedValue}\", \"--env=API_KEY={BackupFiles.RedactedValue}\"]", redacted);
        Assert.Contains($"sse = \"https://mcp.example/sse?token={BackupFiles.RedactedValue}\"", redacted);
        // Escaped spellings are decoded before classifying; unchanged items keep their original spelling.
        Assert.Contains("[mcp_servers.synthetic.\"http\\u005fheaders\"]", redacted);
        Assert.Contains($"args = [\"--api\\u002dkey\", \"{BackupFiles.RedactedValue}\"]", redacted);
        Assert.Contains($"url = \"https://mcp.example/sse?api_key={BackupFiles.RedactedValue}\"", redacted);
        Assert.Contains("\"\\u0065nv\" = {}", redacted);
    }

    [Fact]
    public async Task PluginMcpJsonAndCliTomlReachTheArchiveWithoutCredentials()
    {
        Write(Path.Combine(Home, ".claude", "plugins", "marketplaces", "acme", "plugins", "deploy", ".mcp.json"),
            "{\"mcpServers\":{\"deploy\":{\"command\":\"npx\",\"args\":[\"deploy-mcp\",\"--api-key\",\"json-args-leak\",\"--header=Authorization: Bearer json-nested-leak\",\"--env=API_KEY=json-env-arg-leak\"],\"env\":{\"DEPLOY_KEY\":\"json-env-leak\"},\"headers\":{\"X-Auth\":\"json-header-leak\"}},"
            + "\"remote\":{\"type\":\"http\",\"url\":\"https://mcp.example/mcp?api_key=json-url-leak\"},\"pct\":{\"url\":\"https://mcp.example/mcp?api%5Fkey=json-pct-leak\"},\"esc\":{\"args\":[\"--api\\u002dkey\",\"json-escaped-leak\"]},\"oc\":{\"command\":[\"npx\",\"server\",\"--token\",\"json-command-leak\"]},"
            + "\"hdr\":{\"command\":\"mcp-remote\",\"args\":[\"https://mcp.example/sse\",\"--header\",\"X-Deployment: json-header-arg-leak\"]},"
            + "\"compact\":{\"command\":\"server --password \\\"prefix json-quoted-leak suffix\\\" --port 3000\",\"args\":[\"-HX-Deployment: json-compact-header-leak\",\"-eCUSTOM=json-compact-env-leak\"]}}}");
        Write(Path.Combine(Home, ".claude", "plugins", "marketplaces", "acme", "plugins", "deploy", ".claude-plugin", "plugin.json"),
            "﻿{\"name\":\"deploy\",\"mcpServers\":{\"deploy\":{\"env\":{\"TOKEN\":\"manifest-leak\"}}}}");
        Write(Path.Combine(Home, ".codex", "config.toml"), "model = \"gpt-5\"\n[mcp_servers.github.env]\nGITHUB_TOKEN = \"codex-leak\"\n"
            + "[mcp_servers.remote]\nargs = [\"--header=Authorization: Bearer codex-nested-leak\"]\n"
            + "[mcp_servers.remote.\"http\\u005fheaders\"]\nX-Deployment = \"codex-escaped-header-leak\"\n"
            // VIBE-37: a flag and its value in different quote styles, and a header argument under an arbitrary name.
            + "[mcp_servers.multi]\nargs = [\"--api-key\", \"\"\"codex-multiline-value-leak\"\"\"]\n"
            + "[mcp_servers.literal]\nargs = ['''--token''', \"codex-literal-flag-leak\", \"--header\", \"X-Deployment: codex-header-arg-leak\"]\n"
            + "[mcp_servers.compact]\nargs = [\"-HX-Deployment: codex-compact-header-leak\", \"-eCUSTOM=codex-compact-env-leak\"]\n");
        Write(Path.Combine(Home, ".grok", "config.toml"), "[mcp_servers.search]\ncommand = \"search-mcp\"\nargs = [\"--token\", \"grok-leak\"]\n");
        var files = await Enumerate();

        var work = Path.Combine(root, "work"); Directory.CreateDirectory(work);
        using var output = new MemoryStream();
        Assert.Empty(await BackupFiles.WriteZipAsync(output, files, new SqliteDatabaseSnapshotStore(), work, Ct));
        output.Position = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Read);

        Assert.Equal(5, zip.Entries.Count); // four configuration files and files.json
        foreach (var entry in zip.Entries.Where(e => e.FullName != "files.json"))
            Assert.DoesNotContain("leak", await Read(zip, entry.FullName));
        Assert.Contains("deploy-mcp", await Read(zip, "native/.claude/plugins/marketplaces/acme/plugins/deploy/.mcp.json"));
        Assert.Contains("\"name\":\"deploy\"", await Read(zip, "native/.claude/plugins/marketplaces/acme/plugins/deploy/.claude-plugin/plugin.json"));
        Assert.Contains("\"X-Deployment:" + BackupFiles.RedactedValue + "\"", await Read(zip, "native/.claude/plugins/marketplaces/acme/plugins/deploy/.mcp.json"));
        var codex = await Read(zip, "native/.codex/config.toml");
        Assert.Contains("model = \"gpt-5\"", codex);
        Assert.Contains($"[mcp_servers.multi]\nargs = [\"--api-key\", \"\"\"{BackupFiles.RedactedValue}\"\"\"]", codex);
        Assert.Contains($"[mcp_servers.literal]\nargs = ['''--token''', \"{BackupFiles.RedactedValue}\", \"--header\", \"X-Deployment:{BackupFiles.RedactedValue}\"]", codex);
        Assert.Contains($"[mcp_servers.compact]\nargs = [\"-HX-Deployment:{BackupFiles.RedactedValue}\", \"-eCUSTOM={BackupFiles.RedactedValue}\"]", codex);
        Assert.Contains("\"-HX-Deployment:" + BackupFiles.RedactedValue + "\"", await Read(zip, "native/.claude/plugins/marketplaces/acme/plugins/deploy/.mcp.json"));
        Assert.Contains("command = \"search-mcp\"", await Read(zip, "native/.grok/config.toml"));
    }

    [Fact]
    public async Task ConfiguredHomesAreCapturedWhateverTheEnvironmentIsCalled()
    {
        // Environment names are labels: "Auth review" or "token-cache" must not read as a credential or cache store.
        var envs = Path.Combine(root, "install", "envs");
        Write(Path.Combine(envs, "Auth review", "claude", "settings.json"), "{\"model\":\"opus\"}");
        Write(Path.Combine(envs, "Auth review", "claude", "CLAUDE.md"), "keep");
        Write(Path.Combine(envs, "Auth review", "claude", ".credentials.json"), "leak");
        var outside = Path.Combine(root, "token-cache", "codex");
        Write(Path.Combine(outside, "config.toml"), "model = \"gpt-5\"");
        Write(Path.Combine(envs, "Unconfigured", "codex", "AGENTS.md"), "keep");

        var files = await Enumerate(
            new LLM_Environment { CustomName = "Auth review", LLM = VibeRails.Services.LLM.Claude, Path = Path.Combine(envs, "Auth review", "claude") },
            new LLM_Environment { CustomName = "token-cache", LLM = VibeRails.Services.LLM.Codex, Path = outside },
            new LLM_Environment { CustomName = "Gone", LLM = VibeRails.Services.LLM.Codex, Path = Path.Combine(envs, "Gone", "codex") });
        var entries = files.Select(f => f.Entry).ToList();

        Assert.Contains("installation/envs/Auth review/claude/settings.json", entries);
        Assert.Contains("installation/envs/Auth review/claude/CLAUDE.md", entries);
        Assert.DoesNotContain(entries, e => e.EndsWith(".credentials.json", StringComparison.Ordinal));
        Assert.Single(entries, e => e.EndsWith("/token-cache/codex/config.toml", StringComparison.Ordinal) || e.StartsWith("environments/", StringComparison.Ordinal) && e.EndsWith("/config.toml", StringComparison.Ordinal));
        Assert.Contains("installation/envs/Unconfigured/codex/AGENTS.md", entries);
        Assert.Equal(entries.Count, entries.Distinct().Count());
        Assert.Equal("Configured environment directory is missing.", Assert.Single(files, f => f.Entry == "installation/envs/Gone/codex").Issue);
    }

    private async Task<List<BackupFileSource>> Enumerate(params LLM_Environment[] environments)
    {
        var state = Path.Combine(root, "state.db");
        SqliteStorage.EnsureAllSchemas(new(state));
        var services = new ServiceCollection().AddSqliteStateStorage(_ => new SqliteStoragePaths(state)).BuildServiceProvider();
        using (var scope = services.CreateScope())
            foreach (var environment in environments)
                await scope.ServiceProvider.GetRequiredService<VibeRails.DB.IEnvironmentStore>().SaveEnvironmentAsync(environment, Ct);
        var install = Path.Combine(root, "install"); Directory.CreateDirectory(install);
        return await new BackupFiles(services.GetRequiredService<IServiceScopeFactory>()).EnumerateAsync(install, Home, Ct);
    }

    private static void Write(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text); }
    private static async Task<string> Read(ZipArchive zip, string entry) { using var r = new StreamReader(zip.GetEntry(entry)!.Open()); return await r.ReadToEndAsync(); }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
}
