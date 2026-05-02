namespace VibeRails.DTOs
{
    public class CodexSettingsDto
    {
        public string AskForApproval { get; set; } = "";          // untrusted | on-request | never; empty = use Codex default
        public bool Yolo { get; set; } = false;
        public bool FullAuto { get; set; } = false;
        public bool NoAltScreen { get; set; } = false;
        public bool Oss { get; set; } = false;
        public string Prompt { get; set; } = "";
        public string Model { get; set; } = "";                       // e.g. gpt-5.5, gpt-5.4; empty = use Codex default
        public string Effort { get; set; } = "";                      // minimal | low | medium | high | xhigh; empty = use Codex default
        public bool FastMode { get; set; } = false;                   // persists service_tier = "fast" for supported ChatGPT-backed models
    }
}
