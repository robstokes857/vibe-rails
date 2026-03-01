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
        public static async Task<StartUpStatus> StartUpChecks(IServiceProvider serviceProvider)
        {
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();

            // Initialize version info and app settings
            VersionInfo.Initialize(configuration);
            InitAppSettings(configuration);

            using var scope = serviceProvider.CreateScope();
            var fileService = scope.ServiceProvider.GetRequiredService<IFileService>();
            fileService.InitGlobalSave();

            // Initialize SQLite database
            var dbService = scope.ServiceProvider.GetRequiredService<IDbService>();
            dbService.InitializeDatabase();


            var isLocal = fileService.TryGetProjectRootPath();
            ParserConfigs.SetRootPath(isLocal.projectRoot);

            //Launch for global context only
            if (!isLocal.inGet)
            {
                return StartUpStatus.RequirementsNotMet_NotInGIT;
            }

            fileService.InitLocal(isLocal.projectRoot);

            // TODO: Re-enable hook auto-install once Windows security blocking is resolved (upcoming story)
            // await TryInstallPreCommitHookAsync(scope.ServiceProvider, isLocal.projectRoot);
            return StartUpStatus.Success;
        }

        public static void InitAppSettings(IConfiguration configuration)
        {
            // Load FrontendUrl from appsettings.json
            var frontendUrl = configuration["VibeRails:FrontendUrl"] ?? throw new InvalidOperationException("VibeRails:FrontendUrl is not configured in appsettings.json");
            ParserConfigs.SetFrontendUrl(frontendUrl);

            // Load ApiKey and RemoteAccess from settings.json in ~/.vibe_rails/
            var settings = Config.Load();
            ParserConfigs.SetRemoteAccess(settings.RemoteAccess);
            ParserConfigs.SetApiKey(settings.ApiKey);
        }
    }
}
