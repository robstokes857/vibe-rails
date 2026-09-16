using VibeRails.Services;

namespace VibeRails.DTOs
{
    /// <summary>
    /// Where an environment's CLI actually runs. <see cref="Persistent"/> and
    /// <see cref="PerRun"/> are the same mechanism — a git clone of the project, backed by a
    /// row in Sandboxes — and differ only in retention, which is why this is one setting
    /// rather than two features.
    /// </summary>
    public enum EnvironmentWorkspaceMode
    {
        /// <summary>Run in the project directory. The original behaviour and the default.</summary>
        Project = 0,
        /// <summary>One clone, created on first launch and reused by every launch after it.</summary>
        Persistent = 1,
        /// <summary>A fresh clone per launch, with older ones pruned by retention.</summary>
        PerRun = 2
    }

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
        // When true this environment is an Automation Worker: it was created from the
        // Automation editor to back an automation, is excluded from every launch picker
        // and from the LLM-picker preferences catalog (regardless of Hidden), and is
        // offered by the automation editor's Worker picker instead. Set at creation only.
        public bool AutomationWorker { get; set; }
        // Where this environment's CLI runs — the project directory, a persistent clone, or a
        // fresh clone per launch. Clone modes are backed by a Sandboxes row pointing back here.
        public EnvironmentWorkspaceMode WorkspaceMode { get; set; } = EnvironmentWorkspaceMode.Project;
        // The project this environment belongs to. Null means it predates project scoping and
        // stays visible in every project — there is no backfill, so an environment already in
        // use never vanishes from a project. Set for every environment created from now on.
        public string? ProjectPath { get; set; }

        /// <summary>True when this environment runs in a git clone rather than the project directory.</summary>
        public bool UsesWorkspaceClone => WorkspaceMode != EnvironmentWorkspaceMode.Project;

        public static string DefaultPrompt => """
            Before starting work, read the AGENTS.md file in the project root and any .agents.md files in subdirectories for project-specific rules and context.

            When the "viberails-mcp" MCP server is available, use its tools throughout the session -- especially call validate_vca before any git commit and search_history when prior VibeRails session context would help.
            """;
    }
}
