namespace VibeRails.DTOs
{
    public class GeminiSettingsDto
    {
        public string Theme { get; set; } = "Default";
        public bool SandboxEnabled { get; set; } = true;
        public bool AutoApproveTools { get; set; } = false;
        public bool VimMode { get; set; } = false;
        public bool CheckForUpdates { get; set; } = true;
        public string ApprovalMode { get; set; } = "default";
        public bool YoloMode { get; set; } = false;
    }
}
