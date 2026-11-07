namespace VibeRails.Utils;

/// <summary>
/// The argv shapes retired with custom environments and multi-step Automations (VIBE-292).
/// <c>vb --env &lt;base-cli&gt; --workdir &lt;dir&gt; [-- ...]</c> is still live: it is what
/// <see cref="VibeRails.Services.LlmClis.Launchers.BaseLlmCliLauncher.BuildVbArgv"/> emits for a
/// native-terminal base CLI launch (the sandbox launch route), and the CLI runs next to its own
/// web viewer. Everything after <c>--</c> belongs to that CLI, so it is never inspected.
/// </summary>
public static class RetiredLaunchArguments
{
    public const string Message = "Custom environments and multi-step Automations are retired. Select an AI Agent or Action.";

    /// <summary>
    /// True for <c>--env-id</c>, <c>--job-run</c>, the never-parsed <c>--env=</c> form, and an
    /// <c>--env</c> whose value is not a base CLI (a custom environment name, or no value).
    /// </summary>
    public static bool IsRetired(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg == "--") return false;
            if (Is(arg, "--job-run") || Is(arg, "--env-id") || arg.StartsWith("--env=", StringComparison.OrdinalIgnoreCase))
                return true;
            if (!arg.Equals("--env", StringComparison.OrdinalIgnoreCase)) continue;
            // ArgumentParser takes the next token as the value unless it starts with '-'.
            var value = i + 1 < args.Count && !args[i + 1].StartsWith('-') ? args[i + 1] : null;
            if (VibeRails.Services.LlmParser.ParseValue(value) == VibeRails.Services.LLM.NotSet) return true;
        }
        return false;
    }

    private static bool Is(string arg, string option) =>
        arg.Equals(option, StringComparison.OrdinalIgnoreCase) || arg.StartsWith(option + "=", StringComparison.OrdinalIgnoreCase);
}
