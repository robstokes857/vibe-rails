namespace VibeRails.DTOs
{
    public class CodexSettingsDto
    {
        // Fields persisted to config.toml.
        public string Model { get; set; } = "";                       // model; e.g. gpt-5.5, gpt-5.4; empty = use Codex default
        public string Effort { get; set; } = "";                      // model_reasoning_effort; minimal | low | medium | high | xhigh
        public bool FastMode { get; set; } = false;                   // service_tier = "fast" plus [features].fast_mode
        public bool NoAltScreen { get; set; } = false;                // tui.alternate_screen = "never"

        // Permission posture is YOLO-or-nothing. YOLO is launch-only
        // (CustomArgs: --dangerously-bypass-approvals-and-sandbox); VibeRails never reads or
        // edits Codex's approval_policy / sandbox_mode. Carried here only for the settings payload.
        public bool Yolo { get; set; } = false;
    }
}
