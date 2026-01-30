namespace VibeRails.DTOs
{
    public class ClaudeSettingsDto
    {
        public string Model { get; set; } = "";                  // sonnet | opus | haiku | full model name
        public string PermissionMode { get; set; } = "default";  // default | plan | bypassPermissions
        public string AllowedTools { get; set; } = "";           // Comma-separated list of tools to auto-approve
        public string DisallowedTools { get; set; } = "";        // Comma-separated list of tools to disable
        public bool SkipPermissions { get; set; } = false;       // --dangerously-skip-permissions
        public bool Verbose { get; set; } = false;               // Enable verbose logging
    }
}
