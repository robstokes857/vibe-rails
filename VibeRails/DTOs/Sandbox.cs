namespace VibeRails.DTOs
{
    public class Sandbox
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string Path { get; set; } = "";
        public string ProjectPath { get; set; } = "";
        public string Branch { get; set; } = "";
        public string? CommitHash { get; set; }
        public DateTime CreatedUTC { get; set; } = DateTime.UtcNow;
    }
}
