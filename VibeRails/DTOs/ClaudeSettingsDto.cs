namespace VibeRails.DTOs
{
    public class ClaudeSettingsDto
    {
        // Fields persisted to settings.json.
        public string Effort { get; set; } = "";                                // effortLevel; low | medium | high | xhigh | max
        public string Model { get; set; } = "";                                 // model; pinned IDs e.g. claude-opus-4-8; empty = Claude default
        public bool FastMode { get; set; } = false;                             // "fastMode": true (same as /fast); no launch flag — Opus-only

        // Permission posture is YOLO-or-nothing: DangerouslySkipPermissions is the single
        // "YOLO" toggle. These are launch-only flags carried in CustomArgs, NOT settings.json —
        // VibeRails never reads or edits Claude's permissions block.
        public bool DangerouslySkipPermissions { get; set; } = false;           // YOLO -> --dangerously-skip-permissions
        public bool NoSessionPersistence { get; set; } = false;                 // --no-session-persistence
        public string SystemPrompt { get; set; } = "";                          // --system-prompt
        public bool Bare { get; set; } = false;                                 // --bare
        public bool Debug { get; set; } = false;                                // --debug
    }
}
