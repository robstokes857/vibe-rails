using VibeRails.DB;
using VibeRails.Interfaces;
using VibeRails.Services;
using VibeRails.Utils;

namespace VibeRails.Jobs;

public sealed class ProjectCacheRefreshJob(
    ILogger<ProjectCacheRefreshJob> logger,
    IServiceScopeFactory scopeFactory) : BackgroundService
{
    private const string LastRunCacheKey = "ProjectCacheRefreshJob_LastRun";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small delay to let startup finish
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IRepository>();
            var projectPath = ParserConfigs.GetRootPath();

            if (string.IsNullOrEmpty(projectPath))
                return;

            // Check if we've already run today
            var lastRun = await repository.GetProjectCacheValueAsync(projectPath, LastRunCacheKey, stoppingToken);
            if (lastRun != null && DateTime.TryParse(lastRun, out var lastRunDate) && lastRunDate.Date == DateTime.UtcNow.Date)
            {
                logger.LogDebug("[ProjectCacheRefreshJob] Already ran today, skipping");
                return;
            }

            logger.LogInformation("[ProjectCacheRefreshJob] Running daily cache refresh for {Path}", projectPath);

            var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();
            var isLocal = await fileService.TryGetProjectRootPathAsync(projectPath, stoppingToken);

            await repository.SetProjectCacheValueAsync(projectPath, ProjectCacheKeys.IsGitRepo, isLocal.inGet.ToString());

            if (!isLocal.inGet)
            {
                logger.LogInformation("[ProjectCacheRefreshJob] {Path} is no longer a git repo", projectPath);
                // Git is optional — keep the launch directory as the working root rather
                // than blanking it (which would undo the non-git fallback).
                ParserConfigs.SetGitState(projectPath, isInGit: false);
                await repository.SetProjectCacheValueAsync(projectPath, LastRunCacheKey, DateTime.UtcNow.ToString("O"));
                return;
            }

            await repository.SetProjectCacheValueAsync(projectPath, ProjectCacheKeys.GitRootPath, isLocal.projectRoot);

            try
            {
                var gitService = new GitService(isLocal.projectRoot);
                var branch = await gitService.GetCurrentBranchAsync(stoppingToken);
                var remoteUrl = await gitService.GetRemoteUrlAsync(stoppingToken);

                if (branch != null)
                    await repository.SetProjectCacheValueAsync(projectPath, ProjectCacheKeys.GitBranch, branch);
                if (remoteUrl != null)
                    await repository.SetProjectCacheValueAsync(projectPath, ProjectCacheKeys.GitRemoteUrl, remoteUrl);

                ParserConfigs.SetGitState(isLocal.projectRoot, isInGit: true, gitBranch: branch, gitRemoteUrl: remoteUrl);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[ProjectCacheRefreshJob] Failed to refresh git info");
            }

            await repository.SetProjectCacheValueAsync(projectPath, LastRunCacheKey, DateTime.UtcNow.ToString("O"));
            logger.LogInformation("[ProjectCacheRefreshJob] Daily cache refresh complete for {Path}", projectPath);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[ProjectCacheRefreshJob] Unhandled exception");
        }
    }
}
