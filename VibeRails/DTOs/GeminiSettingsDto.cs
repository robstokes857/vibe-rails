namespace VibeRails.DTOs
{
    public class GeminiSettingsDto
    {
        // Fields persisted to settings.json.
        public string Theme { get; set; } = "Default";
        public bool SandboxEnabled { get; set; } = true;
        public bool VimMode { get; set; } = false;
        public bool CheckForUpdates { get; set; } = true;

        // Permission posture is YOLO-or-nothing. YOLO is launch-only
        // (CustomArgs: --approval-mode yolo); VibeRails never reads or edits Gemini's
        // defaultApprovalMode. Carried here only for the settings payload.
        public bool YoloMode { get; set; } = false;
    }
}
