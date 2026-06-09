using Serilog;
using VibeRails.DB;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Utils;

namespace VibeRails
{

    public enum StartUpStatus
    {
        Success,
        Failed,
        RequiresRestart
    }

    public static class Init
    {
        /// <summary>
        /// Synchronous startup: DB init, app settings, global save.
        /// Git detection runs in the background via <see cref="StartGitDetectionAsync"/>.
        /// </summary>
        public static void StartUpChecks(IServiceProvider serviceProvider)
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();

            VersionInfo.Initialize(configuration);
            InitAppSettings(configuration);

            using var scope = serviceProvider.CreateScope();
            var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();
            fileService.InitGlobalSave();

            var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
            repository.InitializeDatabase();
        }

        /// <summary>
        /// Runs git detection in the background. Checks the ProjectCache first for a fast path,
        /// then falls back to spawning a git process. Updates <see cref="ParserConfigs"/> when done.
        /// </summary>
        public static Task<StartUpStatus> StartGitDetectionAsync(IServiceProvider serviceProvider, string? launchDirectory = null)
        {
            return Task.Run(async () =>
            {
                try
                {
                    using var scope = serviceProvider.CreateScope();
                    var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();
                    var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
                    var projectPath = launchDirectory ?? Directory.GetCurrentDirectory();

                    // Fast path: check ProjectCache for cached git detection
                    var cachedIsGit = await repository.GetProjectCacheValueAsync(projectPath, ProjectCacheKeys.IsGitRepo);

                    if (cachedIsGit != null)
                    {
                        var isGit = string.Equals(cachedIsGit, "true", StringComparison.OrdinalIgnoreCase);

                        if (isGit)
                        {
                            var cachedRoot = await repository.GetProjectCacheValueAsync(projectPath, ProjectCacheKeys.GitRootPath) ?? "";
                            var cachedBranch = await repository.GetProjectCacheValueAsync(projectPath, ProjectCacheKeys.GitBranch);
                            var cachedRemoteUrl = await repository.GetProjectCacheValueAsync(projectPath, ProjectCacheKeys.GitRemoteUrl);

                            ParserConfigs.SetGitState(cachedRoot, isInGit: true, gitBranch: cachedBranch, gitRemoteUrl: cachedRemoteUrl);
                            fileService.InitLocal(cachedRoot);

                            Log.Information("[Init] Fast path: loaded git info from ProjectCache for {Path}", projectPath);

                            // Background refresh to keep cache fresh
                            _ = RefreshProjectCacheInBackgroundAsync(serviceProvider, projectPath);

                            return StartUpStatus.Success;
                        }

                        // Cached as not-a-git-repo — git is optional, so fall back to using the
                        // launch directory as the working root and let the app run normally.
                        // Still refresh in the background in case the user ran git init.
                        ParserConfigs.SetGitState(projectPath, isInGit: false);
                        fileService.InitLocal(projectPath);
                        _ = RefreshProjectCacheInBackgroundAsync(serviceProvider, projectPath);
                        return StartUpStatus.Success;
                    }

                    // Slow path: no cache, run full git detection
                    return await DetectAndCacheGitInfoAsync(fileService, repository, projectPath);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Init] Background git detection failed");
                    // Git is optional — even if detection blows up, fall back to the launch
                    // directory so the app still runs instead of being blocked.
                    var fallbackRoot = launchDirectory ?? Directory.GetCurrentDirectory();
                    ParserConfigs.SetGitState(fallbackRoot, isInGit: false);
                    return StartUpStatus.Success;
                }
            });
        }

        /// <summary>
        /// Runs full git detection, writes results to ProjectCache, and sets ParserConfigs.
        /// </summary>
        private static async Task<StartUpStatus> DetectAndCacheGitInfoAsync(
            IFileService fileService, IRepository repository, string projectPath)
        {
            var isLocal = await fileService.TryGetProjectRootPathAsync(projectPath);

            await repository.SetProjectCacheValueAsync(projectPath, ProjectCacheKeys.IsGitRepo, isLocal.inGet.ToString());

            if (!isLocal.inGet)
            {
                // Git is optional — fall back to the launch directory as the working root
                // so the app runs normally without a .git.
                ParserConfigs.SetGitState(projectPath, isInGit: false);
                fileService.InitLocal(projectPath);
                return StartUpStatus.Success;
            }

            ParserConfigs.SetGitState(isLocal.projectRoot, isInGit: true);
            await repository.SetProjectCacheValueAsync(projectPath, ProjectCacheKeys.GitRootPath, isLocal.projectRoot);
            fileService.InitLocal(isLocal.projectRoot);

            // Fetch branch and remote URL while we're at it
            try
            {
                var gitService = new GitService(isLocal.projectRoot);
                var branch = await gitService.GetCurrentBranchAsync();
                var remoteUrl = await gitService.GetRemoteUrlAsync();

                ParserConfigs.SetGitBranch(branch);
                ParserConfigs.SetGitRemoteUrl(remoteUrl);

                if (branch != null)
                    await repository.SetProjectCacheValueAsync(projectPath, ProjectCacheKeys.GitBranch, branch);
                if (remoteUrl != null)
                    await repository.SetProjectCacheValueAsync(projectPath, ProjectCacheKeys.GitRemoteUrl, remoteUrl);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Init] Failed to fetch branch/remote during git detection");
            }

            return StartUpStatus.Success;
        }

        /// <summary>
        /// Fire-and-forget background refresh: re-runs git detection and updates cache if anything changed.
        /// </summary>
        private static async Task RefreshProjectCacheInBackgroundAsync(IServiceProvider serviceProvider, string projectPath)
        {
            try
            {
                await Task.Delay(500); // small delay to not compete with startup

                using var scope = serviceProvider.CreateScope();
                var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();
                var repository = scope.ServiceProvider.GetRequiredService<IRepository>();

                var isLocal = await fileService.TryGetProjectRootPathAsync(projectPath);

                await repository.SetProjectCacheValueAsync(projectPath, ProjectCacheKeys.IsGitRepo, isLocal.inGet.ToString());

                if (!isLocal.inGet)
                {
                    // Git repo was removed (or never existed) — fall back to the launch
                    // directory as the working root rather than blanking it.
                    ParserConfigs.SetGitState(projectPath, isInGit: false);
                    return;
                }

                await repository.SetProjectCacheValueAsync(projectPath, ProjectCacheKeys.GitRootPath, isLocal.projectRoot);

                // Update branch and remote URL
                var gitService = new GitService(isLocal.projectRoot);
                var branch = await gitService.GetCurrentBranchAsync();
                var remoteUrl = await gitService.GetRemoteUrlAsync();

                if (branch != null)
                    await repository.SetProjectCacheValueAsync(projectPath, ProjectCacheKeys.GitBranch, branch);
                if (remoteUrl != null)
                    await repository.SetProjectCacheValueAsync(projectPath, ProjectCacheKeys.GitRemoteUrl, remoteUrl);

                // Update ParserConfigs with fresh values
                ParserConfigs.SetGitState(isLocal.projectRoot, isInGit: true, gitBranch: branch, gitRemoteUrl: remoteUrl);

                Log.Debug("[Init] Background refresh: updated ProjectCache for {Path}", projectPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Init] Background ProjectCache refresh failed");
            }
        }

        public static void InitAppSettings(IConfiguration configuration)
        {
            var frontendUrl = configuration["VibeRails:FrontendUrl"] ?? throw new InvalidOperationException("VibeRails:FrontendUrl is not configured in appsettings.json");
            ParserConfigs.SetFrontendUrl(frontendUrl);

            var settings = Config.Load();
            ParserConfigs.SetRemoteAccess(settings.RemoteAccess);
            ParserConfigs.SetApiKey(settings.ApiKey);
            ParserConfigs.SetDeveloperOptions(settings.DeveloperOptions);
            ParserConfigs.SetUseVsCodeTheme(settings.UseVsCodeTheme);
        }
    }
}
