namespace VibeRails.DTOs
{
    public class CodexSettingsDto
    {
        public string Model { get; set; } = "";
        public string Sandbox { get; set; } = "read-only";       // read-only | workspace-write | danger-full-access
        public string Approval { get; set; } = "untrusted";      // untrusted | on-failure | on-request | never
        public bool FullAuto { get; set; } = false;
        public bool Search { get; set; } = false;
    }
}
