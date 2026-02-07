namespace VibeRails.Services
{
    public class HookConfiguration
    {
        public bool AutoInstall { get; set; } = true;
        public bool InstallOnStartup { get; set; } = true;
    }

    public class AppConfiguration
    {
        public string Version { get; set; } = "";
        public string InstallDirName { get; set; } = "";
        public string ExecutableName { get; set; } = "";
        public HookConfiguration Hooks { get; set; } = new();
    }
}
