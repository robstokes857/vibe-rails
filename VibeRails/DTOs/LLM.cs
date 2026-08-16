namespace VibeRails.Services
{
    public enum LLM
    {
        NotSet,
        Codex,
        Claude,
        // Google Antigravity CLI — binary is `agy` (mapped in CommandService.PrepareSession).
        Antigravity,
        Copilot,

        // Plain shell terminal — no AI agent. The PTY always spawns a real OS shell
        // (pwsh/zsh/bash); for Shell we simply type no launch command into it.
        Shell,

        // OpenCode CLI — binary is `opencode` (== enum name lowercased, so no executable
        // remap is needed in CommandService.PrepareSession). Per-env config isolation uses
        // XDG_CONFIG_HOME; launch-flag-only (no settings file, like Copilot/Antigravity).
        OpenCode,

        // Pseudo-CLIs: OpenCode launched with a pinned --model flag. The binary is
        // `opencode` (mapped in CommandService.PrepareSession), and the model arg is
        // injected server-side. Enum names can't contain hyphens/periods, so LlmParser
        // special-cases the hyphenated wire names to these values.
        Glm52,
        Grok46
    }
}
