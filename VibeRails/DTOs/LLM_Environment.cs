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
        // When true the environment is excluded from the LLM/terminal select boxes
        // (it can still be launched from the Environments page and used by Automations).
        public bool Hidden { get; set; }

        public static string DefaultPrompt => """
            Before starting work, read the AGENTS.md file in the project root and any .agents.md files in subdirectories for project-specific rules and context.

            When the "viberails-mcp" MCP server is available, use its tools throughout the session -- especially call validate_vca before any git commit and search_history when prior VibeRails session context would help.
            """;
    }
}
