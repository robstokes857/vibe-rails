using System.Text.RegularExpressions;
using VibeRails.DTOs;

namespace VibeRails.Services.LlmClis;

/// <summary>Converts a small, validated launch contract into discrete CLI arguments.</summary>
public static partial class BaseLlmOptionsBuilder
{
    public static BaseLlmOptions? Normalize(LLM llm, BaseLlmOptions? options)
    {
        if (options is null) return null;
        if (llm is LLM.NotSet or LLM.Shell) throw new ArgumentException("This CLI has no model options.");
        var model = options.Model?.Trim() ?? "";
        var effort = options.Effort?.Trim().ToLowerInvariant() ?? "";
        var mode = options.Mode?.Trim().ToLowerInvariant() ?? "";
        if (mode == "default") mode = "";
        // Codex has no startup-mode flag. Its /plan is a TUI command, and the handshake that used
        // to type it into the running TUI was removed 2026-09-15 — nothing types into a TUI. A mode
        // stored on a card from before then is dropped rather than rejected, so those cards still
        // launch instead of failing validation on an option that can no longer be honoured.
        if (llm == LLM.Codex) mode = "";
        if (model.Length > 200 || (model.Length > 0 && !ModelPattern().IsMatch(model)))
            throw new ArgumentException("Invalid model name.");
        var pinned = PinnedModel(llm);
        if (pinned is not null && model.Length > 0 && model != pinned)
            throw new ArgumentException("This CLI uses a fixed model. Select OpenCode to choose another model.");
        // The command service already inserts a pseudo-CLI's fixed model.
        if (pinned is not null) model = "";
        string[] efforts = llm switch
        {
            LLM.Claude => ["low", "medium", "high", "xhigh", "max"],
            LLM.Codex => ["minimal", "low", "medium", "high", "xhigh", "max", "ultra"],
            LLM.Antigravity => ["low", "medium", "high"],
            LLM.Copilot => ["none", "minimal", "low", "medium", "high", "xhigh", "max"],
            LLM.Grok46 => ["low", "medium", "high", "xhigh"],
            _ => []
        };
        if (effort.Length > 0 && !efforts.Contains(effort)) throw new ArgumentException("Unsupported effort for this CLI.");
        if (llm == LLM.Codex && model.Equals("gpt-5.5", StringComparison.OrdinalIgnoreCase) && effort == "max") effort = "xhigh";
        string[] modes = llm switch
        {
            LLM.Antigravity => ["accept-edits", "plan"],
            LLM.Copilot => ["interactive", "plan", "autopilot"],
            LLM.OpenCode or LLM.Glm52 or LLM.Glm53 or LLM.DeepSeekV4Pro or LLM.KimiK3 => ["build", "plan"],
            _ => ["plan"]
        };
        if (mode.Length > 0 && !modes.Contains(mode)) throw new ArgumentException("Unsupported startup mode for this CLI.");
        return model.Length + effort.Length + mode.Length == 0 && !options.Yolo
            ? null
            : new(model, effort, mode, options.Yolo);
    }

    public static string[] BuildArguments(LLM llm, BaseLlmOptions? options)
    {
        var value = Normalize(llm, options);
        if (value is null) return [];
        var args = new List<string>();
        if (!string.IsNullOrEmpty(value.Model)) args.AddRange(["--model", value.Model]);
        if (!string.IsNullOrEmpty(value.Effort))
            args.AddRange(llm == LLM.Codex ? ["-c", $"model_reasoning_effort={value.Effort}"] : ["--effort", value.Effort]);
        // No Codex special case needed: Normalize above has already cleared its mode, so every
        // mode that reaches here has a real provider flag to become.
        if (!string.IsNullOrEmpty(value.Mode))
        {
            var flag = llm switch
            {
                LLM.Claude or LLM.Grok46 => "--permission-mode",
                LLM.Antigravity or LLM.Copilot => "--mode",
                _ => "--agent"
            };
            args.AddRange([flag, value.Mode]);
        }
        if (value.Yolo)
        {
            args.Add(llm switch
            {
                LLM.Codex => "--dangerously-bypass-approvals-and-sandbox",
                LLM.Claude or LLM.Antigravity => "--dangerously-skip-permissions",
                LLM.Copilot or LLM.Grok46 => "--yolo",
                LLM.OpenCode or LLM.Glm52 or LLM.Glm53 or LLM.DeepSeekV4Pro or LLM.KimiK3 => "--auto",
                _ => throw new ArgumentException("This CLI has no YOLO mode.")
            });
        }
        return args.ToArray();
    }

    public static string? PinnedModel(LLM llm) => llm switch
    {
        LLM.Glm52 => "zai/glm-5.2", LLM.Glm53 => "zai-coding-plan/glm-5.3",
        LLM.DeepSeekV4Pro => "deepseek/deepseek-v4-pro", LLM.KimiK3 => "moonshotai/kimi-k3",
        _ => null
    };

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._/: ()\-]*$")]
    private static partial Regex ModelPattern();
}
