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
        Shell
    }
}
