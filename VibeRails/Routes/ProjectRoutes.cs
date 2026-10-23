using System.Diagnostics;
using System.Text;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Services.Git;

namespace VibeRails.Routes;

public static class ProjectRoutes
{
    /// <summary>
    /// Strips userinfo from a git remote URL before it leaves the server. A repo cloned with an
    /// embedded credential stores it verbatim in .git/config (<c>https://user:TOKEN@host/…</c> —
    /// or the GitHub-PAT-as-username form <c>https://TOKEN@host/…</c>), and
    /// <c>git remote get-url</c> returns it unredacted. The UI only derives a display name from
    /// this value; cloning paths (SandboxService) keep using the raw URL internally. scp-style
    /// remotes (<c>git@host:path</c>) don't parse as absolute URIs and carry no password, so they
    /// pass through unchanged.
    /// </summary>
    internal static string? StripUserInfo(string? remoteUrl)
    {
        if (string.IsNullOrEmpty(remoteUrl)
            || !Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri)
            || string.IsNullOrEmpty(uri.UserInfo))
        {
            return remoteUrl;
        }

        return new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.ToString();
    }

    // Dangerous directories that should never be git-initialized
    private static readonly string[] DangerousWindowsPaths =
    [
        @"C:\",
        @"C:\Program Files",
        @"C:\Program Files (x86)",
        @"C:\Windows",
        @"C:\Users",
    ];

    private static readonly string[] DangerousUnixPaths =
    [
        "/",
        "/home",
        "/root",
        "/usr",
        "/etc",
        "/bin",
        "/sbin",
        "/var",
        "/tmp",
        "/Users",
    ];

    // `git init` stages the whole directory, so a profile root or one of its personal folders
    // would sweep keys, documents and app data into a commit. Relative to the user's profile.
    private static readonly string[] ProfileChildFolders =
    [
        "Desktop",
        "Documents",
        "Downloads",
        "Pictures",
        "Music",
        "Videos",
        "Movies",
        "Public",
        "OneDrive",
        "Favorites",
        "Links",
        "Saved Games",
        "AppData",
        "AppData/Local",
        "AppData/LocalLow",
        "AppData/Roaming",
        "Library",
        ".config",
        ".local",
        ".local/share",
        ".cache",
        ".ssh",
        ".vibe_rails",
    ];

    public static void Map(WebApplication app, string launchDirectory)
    {
        var isActiveRootBackend = app.Services
            .GetRequiredService<ProcessRole>()
            .IsActiveRootBackend;

        // Bare readiness probe: the ONLY deliberately unauthenticated endpoint (see
        // CookieAuthMiddleware's skip list). The parent process polls it on a child terminal
        // server BEFORE consuming the child's bootstrap code — i.e. before any token exists —
        // so it must answer without auth and must reveal nothing.
        app.MapGet("/health", () => Results.Ok()).WithName("Health");

        app.MapGet("/api/v1/context", (IGitService gitService) =>
        {
            // Return immediately with whatever git state is known.
            // Git detection runs in the background during startup — ParserConfigs
            // is updated when it completes. The frontend polls or re-fetches as needed.
            var rootPath = Utils.ParserConfigs.GetRootPath();
            var isInGit = Utils.ParserConfigs.GetIsInGit();

            var sandboxesDir = Path.Combine(Utils.PathConstants.GetInstallDirPath(), Utils.PathConstants.SANDBOXES_SUBDIR);
            var isSandbox = launchDirectory.StartsWith(sandboxesDir, StringComparison.OrdinalIgnoreCase);

            return Results.Ok(new ContextResponse(
                IsInGit: isInGit,
                LaunchDirectory: launchDirectory,
                RootPath: rootPath,
                GitBranch: Utils.ParserConfigs.GetGitBranch(),
                GitRemoteUrl: StripUserInfo(Utils.ParserConfigs.GetGitRemoteUrl()),
                IsSandbox: isSandbox,
                IsActiveRootBackend: isActiveRootBackend
            ));
        }).WithName("GetContext");

        // POST /api/v1/git/init — initialize git in the launch directory
        app.MapPost("/api/v1/git/init", async (IFileService fileService, IRepository repository, CancellationToken cancellationToken) =>
        {
            // Sanity check: don't allow git init in dangerous directories
            if (IsDangerousDirectory(launchDirectory))
            {
                return Results.BadRequest(new ErrorResponse(
                    "Cannot initialize git in a system directory, a user profile folder, or one of its personal folders."));
            }

            // Check if git is installed
            if (!await IsGitInstalledAsync(launchDirectory, cancellationToken))
            {
                return Results.BadRequest(new ErrorResponse("Git is not installed or not found in PATH."));
            }

            // Run: git init
            var initResult = await RunGitCommandAsync(["init"], launchDirectory, cancellationToken);
            if (!initResult.Success)
            {
                return Results.BadRequest(new ErrorResponse($"git init failed: {initResult.Output}"));
            }

            // Run: git add .
            var addResult = await RunGitCommandAsync(["add", "."], launchDirectory, cancellationToken);
            if (!addResult.Success)
            {
                return Results.BadRequest(new ErrorResponse($"git add failed: {addResult.Output}"));
            }

            // Run: git commit -m "Initial commit" — may fail if nothing to commit, that's fine
            await RunGitCommandAsync(["commit", "-m", "Initial commit"], launchDirectory, cancellationToken);

            // Re-detect git root and update state
            var detected = await fileService.TryGetProjectRootPathAsync(cancellationToken: cancellationToken);

            if (!detected.inGet)
            {
                // Leave the existing launch-directory fallback in place rather than
                // blanking RootPath in this rare "init succeeded but root undetectable" case.
                return Results.BadRequest(new ErrorResponse("Git initialized but could not detect repository root."));
            }

            Utils.ParserConfigs.SetGitState(detected.projectRoot, isInGit: true);
            fileService.InitLocal(detected.projectRoot);

            await global::VibeRails.Init.TryInstallGitHooksIfEnabledAsync(
                app.Services,
                detected.projectRoot,
                cancellationToken);

            // Update ProjectCache with new git state
            await repository.SetProjectCacheValueAsync(launchDirectory, Services.ProjectCacheKeys.IsGitRepo, "True");
            await repository.SetProjectCacheValueAsync(launchDirectory, Services.ProjectCacheKeys.GitRootPath, detected.projectRoot);

            // Fetch and cache branch info
            try
            {
                var gitService = new Services.GitService(detected.projectRoot);
                var branch = await gitService.GetCurrentBranchAsync(cancellationToken);
                var remoteUrl = await gitService.GetRemoteUrlAsync(cancellationToken);

                Utils.ParserConfigs.SetGitBranch(branch);
                Utils.ParserConfigs.SetGitRemoteUrl(remoteUrl);

                if (branch != null)
                    await repository.SetProjectCacheValueAsync(launchDirectory, Services.ProjectCacheKeys.GitBranch, branch);
                if (remoteUrl != null)
                    await repository.SetProjectCacheValueAsync(launchDirectory, Services.ProjectCacheKeys.GitRemoteUrl, remoteUrl);
            }
            catch { /* non-critical */ }

            return Results.Ok(new ContextResponse(
                IsInGit: true,
                LaunchDirectory: launchDirectory,
                RootPath: detected.projectRoot,
                GitBranch: Utils.ParserConfigs.GetGitBranch(),
                GitRemoteUrl: Utils.ParserConfigs.GetGitRemoteUrl(),
                IsActiveRootBackend: isActiveRootBackend
            ));
        }).WithName("GitInit");

        // POST /api/v1/git/open-directory — spawn a new vb instance in the given directory
        app.MapPost("/api/v1/git/open-directory", async (GitOpenDirectoryRequest request, CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Directory))
            {
                return Results.BadRequest(new ErrorResponse("Directory is required."));
            }

            if (!Directory.Exists(request.Directory))
            {
                return Results.BadRequest(new ErrorResponse("Directory does not exist."));
            }

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return Results.BadRequest(new ErrorResponse("Unable to determine executable path."));
            }

            var bootstrapTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = "--vs-code-v1",
                    WorkingDirectory = request.Directory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    StandardOutputEncoding = Encoding.UTF8,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, args) =>
            {
                if (string.IsNullOrWhiteSpace(args.Data)) return;
                var line = args.Data.Trim();
                if (!line.StartsWith("vs-code-v1=", StringComparison.OrdinalIgnoreCase)) return;
                var url = line["vs-code-v1=".Length..].Trim();
                if (!string.IsNullOrWhiteSpace(url))
                    bootstrapTcs.TrySetResult(url);
            };

            process.Exited += (_, _) =>
            {
                if (!bootstrapTcs.Task.IsCompleted)
                    bootstrapTcs.TrySetException(new InvalidOperationException("New vb instance exited before producing bootstrap URL."));
            };

            if (!process.Start())
            {
                return Results.BadRequest(new ErrorResponse("Failed to start new vb instance."));
            }

            process.BeginOutputReadLine();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            try
            {
                var bootstrapUrl = await bootstrapTcs.Task.WaitAsync(timeoutCts.Token);
                return Results.Ok(new GitOpenDirectoryResponse(bootstrapUrl));
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return Results.BadRequest(new ErrorResponse("Timed out waiting for new instance to start."));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[GitOpenDirectory] Failed to get bootstrap URL from new instance");
                return Results.BadRequest(new ErrorResponse($"Failed to open directory: {ex.Message}"));
            }
        }).WithName("GitOpenDirectory");

        // PUT /api/v1/projects/name - Set project display name on the most recent session for this path
        app.MapPut("/api/v1/projects/name", async (
            IRepository repository,
            UpdateAgentNameRequest request,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(request.Path))
            {
                return Results.BadRequest(new ErrorResponse("Path is required"));
            }

            if (string.IsNullOrEmpty(request.CustomName))
            {
                return Results.BadRequest(new ErrorResponse("CustomName is required"));
            }

            var updated = await repository.UpdateLatestProjectDisplayNameAsync(request.Path, request.CustomName, cancellationToken);
            if (!updated)
            {
                return Results.BadRequest(new ErrorResponse("Start a chat session before setting a project name."));
            }

            return Results.Ok(new UpdateAgentNameResponse(request.Path, request.CustomName));
        }).WithName("UpdateProjectName");

        // GET /api/v1/projects/name?path={path} - Get project display name for the current path
        app.MapGet("/api/v1/projects/name", async (
            IRepository repository,
            string path,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrEmpty(path))
            {
                return Results.BadRequest(new ErrorResponse("Path is required"));
            }

            var projectDisplayName = await repository.GetProjectDisplayNameAsync(path, cancellationToken);
            return Results.Ok(new UpdateAgentNameResponse(path, projectDisplayName));
        }).WithName("GetProjectName");
    }

    private static bool IsDangerousDirectory(string path) =>
        IsDangerousDirectory(
            path,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            GetKnownUserFolders());

    /// <summary>
    /// True for a system directory, a user profile root (this user's or a sibling such as
    /// <c>C:\Users\&lt;name&gt;</c>), or a well-known personal folder inside this user's profile.
    /// Folders below those, such as <c>~/Documents/my-project</c>, stay allowed.
    /// </summary>
    internal static bool IsDangerousDirectory(
        string path,
        string? userProfile,
        IEnumerable<string?> knownUserFolders)
    {
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var normalizedPath = NormalizeDirectory(path);
        bool Matches(string candidate) =>
            string.Equals(normalizedPath, NormalizeDirectory(candidate), comparison);

        if (OperatingSystem.IsWindows())
        {
            if (DangerousWindowsPaths.Any(Matches))
                return true;
            // Also block a drive root (one segment, e.g. C:\)
            var parts = normalizedPath.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                return true;
        }
        else if (DangerousUnixPaths.Any(Matches))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            var profile = NormalizeDirectory(userProfile);
            if (Matches(profile) || ProfileChildFolders.Any(child => Matches(Path.Combine(profile, child))))
                return true;

            // Any profile next to this user's (C:\Users\Public, /home/other). Skipped when the
            // profile sits directly under a filesystem root (root's /root), where the siblings
            // are ordinary top-level directories such as /srv.
            var usersRoot = Path.GetDirectoryName(profile);
            if (usersRoot != null
                && Path.GetDirectoryName(usersRoot) != null
                && string.Equals(Path.GetDirectoryName(normalizedPath), usersRoot, comparison))
            {
                return true;
            }
        }

        return knownUserFolders.Any(folder => !string.IsNullOrWhiteSpace(folder) && Matches(folder));
    }

    /// <summary>
    /// One spelling per directory, so a blocked folder cannot be reached by another name: the
    /// <c>\\?\</c> / <c>\\.\</c> device prefixes are dropped, a symbolic link or junction is
    /// replaced by its final target, and a Windows 8.3 short name (<c>ROBERT~1</c>) by the long
    /// name. Components that do not exist are kept as written.
    /// </summary>
    internal static string NormalizeDirectory(string path)
    {
        var full = Path.GetFullPath(StripDevicePrefix(path));
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
            return Path.TrimEndingDirectorySeparator(full);

        var resolved = root;
        foreach (var segment in full[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = ResolveSegment(resolved, segment);
        }

        return Path.TrimEndingDirectorySeparator(resolved);
    }

    private static string ResolveSegment(string parent, string segment)
    {
        var next = Path.Combine(parent, segment);
        try
        {
            var directory = new DirectoryInfo(next);
            if (directory.LinkTarget is not null)
            {
                var target = directory.ResolveLinkTarget(returnFinalTarget: true);
                return target is null ? next : Path.GetFullPath(StripDevicePrefix(target.FullName));
            }

            // FindFirstFile matches short names too and reports the long one.
            if (OperatingSystem.IsWindows() && segment.Contains('~') && directory.Exists)
            {
                var longName = new DirectoryInfo(parent).EnumerateDirectories(segment).FirstOrDefault()?.Name;
                if (!string.IsNullOrEmpty(longName))
                    return Path.Combine(parent, longName);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unreadable component: compare it as written.
        }

        return next;
    }

    private static string StripDevicePrefix(string path)
    {
        if (!OperatingSystem.IsWindows())
            return path;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)
            || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith(@"\??\", StringComparison.Ordinal))
            return path[4..];
        return path;
    }

    /// <summary>Personal folders the OS may have redirected out of the profile (OneDrive backup).</summary>
    private static IEnumerable<string?> GetKnownUserFolders() =>
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("OneDrive"),
        Environment.GetEnvironmentVariable("OneDriveConsumer"),
        Environment.GetEnvironmentVariable("OneDriveCommercial"),
    ];

    /// <summary>A missing git binary surfaces as a failed start inside <see cref="GitCli"/>, never as a thrown exception.</summary>
    private static async Task<bool> IsGitInstalledAsync(string workingDirectory, CancellationToken cancellationToken)
        => (await GitCli.RunAsync(workingDirectory, ["--version"], cancellationToken)).Succeeded;

    /// <summary>Runs one argv git command through the shared launch contract; output is stdout then stderr, trimmed.</summary>
    private static async Task<(bool Success, string Output)> RunGitCommandAsync(
        IReadOnlyList<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await GitCli.RunAsync(workingDirectory, arguments, cancellationToken, timeout: TimeSpan.FromSeconds(30));
        var output = string.Concat(result.StdOut, result.StdErr).Trim();
        return (result.Succeeded, result.TimedOut ? $"git {string.Join(' ', arguments)} timed out. {output}".Trim() : output);
    }
}
