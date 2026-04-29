namespace VibeRails.DTOs
{
    public class CodexSettingsDto
    {
        public string AskForApproval { get; set; } = "untrusted"; // untrusted | on-request | never
        public bool Yolo { get; set; } = false;
        public bool FullAuto { get; set; } = false;
        public bool NoAltScreen { get; set; } = false;
        public bool Oss { get; set; } = false;
        public string Prompt { get; set; } = "";
    }
}
