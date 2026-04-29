namespace VibeRails.DTOs
{
    public class ClaudeSettingsDto
    {
        public string Effort { get; set; } = "";                                // low | medium | high | xhigh | max
        public bool NoSessionPersistence { get; set; } = false;                 // --no-session-persistence
        public string PermissionMode { get; set; } = "default";                 // default | acceptEdits | plan | auto | dontAsk | bypassPermissions
        public string SystemPrompt { get; set; } = "";                         // --system-prompt
        public bool AllowDangerouslySkipPermissions { get; set; } = false;      // --allow-dangerously-skip-permissions
        public string DangerouslyLoadDevelopmentChannels { get; set; } = "";    // --dangerously-load-development-channels
        public bool DangerouslySkipPermissions { get; set; } = false;           // --dangerously-skip-permissions
        public string AllowedTools { get; set; } = "";                         // --allowedTools
        public string AppendSystemPrompt { get; set; } = "";                   // --append-system-prompt
        public bool Bare { get; set; } = false;                                 // --bare
        public string Betas { get; set; } = "";                                // --betas
        public string Channels { get; set; } = "";                             // --channels
        public bool Debug { get; set; } = false;                                // --debug
        public string DebugFilter { get; set; } = "";                          // optional --debug category filter
    }
}
