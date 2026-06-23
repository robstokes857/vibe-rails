using VibeRails.DTOs;

namespace VibeRails.Services
{
    /// <summary>
    /// Well-known keys for the project-agnostic <see cref="IGlobalCache"/>.
    /// </summary>
    public static class GlobalCacheKeys
    {
        /// <summary>
        /// Per-CLI record of whether we have already removed the VibeRails MCP server from
        /// that CLI's user-scope registration while the MCP feature is opted-out. Tracked
        /// per CLI because each CLI is a separate registration — turning MCP off must clean
        /// every CLI independently, and we only want to issue the `mcp remove` once per CLI
        /// (not on every session launch). Reset when the user opts back in.
        /// </summary>
        public static string McpRemovedFromCli(LLM llm) => $"McpRemovedFromCli:{llm}";
    }
}
