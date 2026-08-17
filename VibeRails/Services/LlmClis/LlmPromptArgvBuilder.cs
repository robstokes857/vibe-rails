using VibeRails.DTOs;

namespace VibeRails.Services.LlmClis;

internal static class LlmPromptArgvBuilder
{
    /// <summary>
    /// Appends an initial prompt to a CLI argv list using the per-LLM convention.
    /// Copilot consumes the prompt via <c>--interactive=&lt;text&gt;</c>; Antigravity (agy)
    /// via <c>--prompt-interactive=&lt;text&gt;</c> (it has no positional-prompt form); OpenCode
    /// via <c>--prompt=&lt;text&gt;</c> (its TUI treats a positional arg as the project path);
    /// every other CLI takes the prompt as a trailing positional argument.
    ///
    /// Called only from CommandService.PrepareSessionAsync (whose shell-string branch mirrors
    /// the same switch). Spawning routes deliberately stopped appending the prompt themselves:
    /// PromptPlaceholderService resolution ({{step:...}} runs a shell command) must happen
    /// exactly once, in the process that owns the PTY, which is where PrepareSessionAsync runs.
    /// </summary>
    public static void AppendInitialPrompt(List<string> argv, LLM llm, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;

        if (llm == LLM.Copilot)
            argv.Add($"--interactive={prompt}");
        else if (llm == LLM.Antigravity)
            argv.Add($"--prompt-interactive={prompt}");
        else if (llm == LLM.OpenCode || llm == LLM.Glm52 || llm == LLM.Grok46 || llm == LLM.Glm53)
            argv.Add($"--prompt={prompt}");
        else
            argv.Add(prompt);
    }
}
