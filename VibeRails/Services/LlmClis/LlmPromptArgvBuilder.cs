using VibeRails.DTOs;

namespace VibeRails.Services.LlmClis;

internal static class LlmPromptArgvBuilder
{
    /// <summary>
    /// Appends an initial prompt to a CLI argv list using the per-LLM convention.
    /// Copilot consumes the prompt via <c>--interactive=&lt;text&gt;</c>; every other
    /// CLI takes the prompt as a trailing positional argument. Centralized so that
    /// every launch path (web start, sandbox launch, bootstrap command) applies the
    /// same convention.
    /// </summary>
    public static void AppendInitialPrompt(List<string> argv, LLM llm, string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return;

        if (llm == LLM.Copilot)
            argv.Add($"--interactive={prompt}");
        else
            argv.Add(prompt);
    }
}
