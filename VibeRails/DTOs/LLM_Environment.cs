using VibeRails.Services;

namespace VibeRails.DTOs
{
    public class LLM_Environment
    {
        public int Id { get; set; }
        public LLM LLM { get; set; }
        public string CustomName { get; set; } = "Default";
        public string Path { get; set; } = "";
        public string CustomArgs { get; set; } = "";
        public string CustomPrompt { get; set; } = "";
        public DateTime CreatedUTC { get; set; } = DateTime.UtcNow;
        public DateTime LastUsedUTC { get; set; } = DateTime.UtcNow;

        public static string DefaultPrompt => """
            Before starting work, read the AGENTS.md file in the project root and any .agents.md files in subdirectories for project-specific rules and context.

            You have a "viberails-mcp" MCP server connected. Use its tools throughout the session -- especially call ValidateVca before any git commit, and SearchUserTerms when the user refers to code informally.
            """;
    }
}
