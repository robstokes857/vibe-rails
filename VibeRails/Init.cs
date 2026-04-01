using Serilog;
using VibeRails.DB;
using VibeRails.Interfaces;
using VibeRails.Utils;

namespace VibeRails
{

    public enum StartUpStatus
    {
        Success,
        Failed,
        RequiresRestart,
        RequirementsNotMet_NotInGIT
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
        /// Runs git detection in the background. Updates <see cref="ParserConfigs"/> when done.
        /// Returns a task that resolves to the startup status.
        /// </summary>
        public static Task<StartUpStatus> StartGitDetectionAsync(IServiceProvider serviceProvider, string? launchDirectory = null)
        {
            return Task.Run(async () =>
            {
                try
                {
                    using var scope = serviceProvider.CreateScope();
                    var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();

                    var isLocal = await fileService.TryGetProjectRootPathAsync(launchDirectory);
                    ParserConfigs.SetRootPath(isLocal.projectRoot);

                    if (!isLocal.inGet)
                    {
                        return StartUpStatus.RequirementsNotMet_NotInGIT;
                    }

                    fileService.InitLocal(isLocal.projectRoot);
                    return StartUpStatus.Success;
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[Init] Background git detection failed");
                    return StartUpStatus.RequirementsNotMet_NotInGIT;
                }
            });
        }

        public static void InitAppSettings(IConfiguration configuration)
        {
            var frontendUrl = configuration["VibeRails:FrontendUrl"] ?? throw new InvalidOperationException("VibeRails:FrontendUrl is not configured in appsettings.json");
            ParserConfigs.SetFrontendUrl(frontendUrl);

            var settings = Config.Load();
            ParserConfigs.SetRemoteAccess(settings.RemoteAccess);
            ParserConfigs.SetApiKey(settings.ApiKey);
            ParserConfigs.SetDeveloperOptions(settings.DeveloperOptions);
        }
    }
}
