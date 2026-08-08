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
        public string? RemoteUrl { get; set; }
        public string? SourceBranch { get; set; }
        public DateTime CreatedUTC { get; set; } = DateTime.UtcNow;
        // The environment that owns this sandbox as its workspace, or null for a standalone
        // sandbox created from the Sandboxes card. Standalone is the pre-fold behaviour and
        // keeps its own "launch any CLI in here" picker; an owned workspace is driven by its
        // environment instead, and an environment delete releases it back to standalone.
        public int? EnvironmentId { get; set; }
    }
}
