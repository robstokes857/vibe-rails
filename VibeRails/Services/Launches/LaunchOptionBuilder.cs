using VibeRails.DTOs;
using VibeRails.Launches;
using VibeRails.Services.LlmClis;
using VibeRails.Utils;

namespace VibeRails.Services.Launches;

/// <summary>Adapts the supported environment controls to session-local argv; never writes a provider home.</summary>
public static class LaunchOptionBuilder
{
    public static LaunchPreset Normalize(LaunchPreset preset)
    {
        if (!Enum.IsDefined(preset.Cli) || preset.Cli is LLM.NotSet or LLM.Shell)
            throw new LaunchValidationException("Select a supported AI CLI.");
        if (string.IsNullOrWhiteSpace(preset.Name) || preset.Name.Trim().Length > 300)
            throw new LaunchValidationException("Name must contain 1–300 characters.");
        if (preset.Description?.Length > 100_000 || preset.InitialPrompt?.Length > 30_000)
            throw new LaunchValidationException("Description or initial prompt exceeds its character limit.");
        LaunchValidation.RejectRetiredPrompt(preset.InitialPrompt);
        if (preset.TimeoutMinutes is <= 0 or > 1440) throw new LaunchValidationException("Timeout must be 1–1440 minutes, or empty.");
        if (!AgentPurpose.IsValid(preset.Purpose)) throw new LaunchValidationException("Unsupported Agent purpose.");
        var options = preset.Options ?? new();
        if (options.Claude is not null && preset.Cli != LLM.Claude
            || options.Codex is not null && preset.Cli != LLM.Codex
            || options.OpenCode is not null && preset.Cli is not (LLM.OpenCode or LLM.Glm52 or LLM.Glm53 or LLM.DeepSeekV4Pro or LLM.KimiK3))
            throw new LaunchValidationException("Provider options must belong to the selected CLI.");
        // The base builder tolerates an old Codex mode. New definitions and old queued snapshots
        // must reject a setting that cannot be honored, rather than silently dropping it.
        if (preset.Cli == LLM.Codex && options.Base?.Mode is { Length: > 0 } mode && mode != "default")
            throw new LaunchValidationException("Codex has no supported startup mode. Clear the saved mode.");
        LaunchValidation.RejectRetiredPrompt(options.Claude?.SystemPrompt);
        if (options.Claude?.SystemPrompt?.Length > 30_000)
            throw new LaunchValidationException("System prompt exceeds 30,000 characters.");
        try { options = options with { Base = BaseLlmOptionsBuilder.Normalize(preset.Cli, options.Base) }; }
        catch (ArgumentException ex) { throw new LaunchValidationException(ex.Message); }
        var argv = preset.AdditionalArgv ?? [];
        if (argv.Length > 64 || argv.Any(arg => arg is null || arg.Length > 8_000 || arg.Any(char.IsControl)))
            throw new LaunchValidationException("Additional argv supports 64 arguments, at most 8,000 characters each, without NUL.");
        foreach (var arg in argv)
        {
            LaunchValidation.RejectRetiredPrompt(arg);
            _ = ShellArgSanitizer.EscapeArg(arg);
        }
        return preset with { Name = preset.Name.Trim(), Description = preset.Description ?? "", Options = options, AdditionalArgv = argv };
    }

    public static string[] Build(LaunchPreset preset)
    {
        preset = Normalize(preset);
        List<string> args = [.. BaseLlmOptionsBuilder.BuildArguments(preset.Cli, preset.Options.Base)];
        if (preset.Options.Claude is { } claude)
        {
            // --settings accepts inline JSON. The payload is constant, never shell text.
            args.AddRange(["--settings", claude.FastMode ? "{\"fastMode\":true}" : "{\"fastMode\":false}"]);
            if (claude.NoSessionPersistence) args.Add("--no-session-persistence");
            if (!string.IsNullOrWhiteSpace(claude.SystemPrompt)) args.AddRange(["--system-prompt", claude.SystemPrompt]);
            if (claude.Bare) args.Add("--bare");
            if (claude.Debug) args.Add("--debug");
        }
        if (preset.Options.Codex?.NoAltScreen == true) args.AddRange(["-c", "tui.alternate_screen=\"never\""]);
        if (preset.Options.OpenCode?.DisablePlugins == true) args.Add("--pure");
        args.AddRange(preset.AdditionalArgv);
        return args.ToArray();
    }
}
