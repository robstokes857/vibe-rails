using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Cli;
using VibeRails.Utils;

namespace VibeRails.Services.Environments;

/// <summary>
/// Where a prompt is being resolved from. WorkingDirectory must already be workspace-resolved
/// (a clone-mode environment resolves {{git_branch}} and runs {{step:...}} commands inside the
/// clone, not the project root). EnvironmentId scopes {{step:&lt;id&gt;}} lookups — a null id means
/// no environment is in play and every step reference resolves to the deleted-step text.
/// </summary>
public sealed record PromptPlaceholderContext(
    string WorkingDirectory,
    int? EnvironmentId = null,
    string? EnvironmentName = null);

/// <summary>
/// Thrown when a prompt is too long to hand to a CLI. Callers turn this into a user-facing error
/// rather than truncating: silently dropping the tail of a prompt changes what the agent was asked
/// to do, and the user has no way to see that it happened.
/// </summary>
public sealed class PromptTooLongException(string message, int actualChars, int maxChars)
    : InvalidOperationException(message)
{
    public int ActualChars { get; } = actualChars;
    public int MaxChars { get; } = maxChars;

    /// <summary>The resolved message itself is over budget — usually a step that printed a lot.</summary>
    public static PromptTooLongException ForResolvedPrompt(int actualChars, int maxChars) => new(
        $"The Initial Message resolved to {actualChars:N0} characters, over the {maxChars:N0} character limit. " +
        "Shorten the message, or reduce the output of the steps it references.",
        actualChars, maxChars);

    /// <summary>
    /// The message fits, but shell-escaping it pushed the assembled launch command past what the
    /// OS will accept. Quote-heavy prompts double (or quadruple) in size, so the two limits are
    /// not interchangeable and the wording has to say which one was hit.
    /// </summary>
    public static PromptTooLongException ForCommandLine(int actualChars, int maxChars) => new(
        $"Escaping the Initial Message produced a {actualChars:N0} character launch command, over the " +
        $"{maxChars:N0} character limit this platform allows. Shorten the message, or remove some of its " +
        "quotes and $ characters — each one doubles in the escaped command.",
        actualChars, maxChars);
}

/// <summary>
/// Resolves the auto-filled placeholders an Initial Message may carry. This must run exactly once
/// per launch, in the process that owns the PTY (TerminalRoutes for web tabs, CliLoop for spawned
/// terminals): {{step:&lt;id&gt;}} executes a shell command, so a second resolution pass would run it
/// a second time — and resolving where the seq-1 UserInputs recording happens is what keeps the
/// recorded text identical to what the CLI actually received.
/// </summary>
public interface IPromptPlaceholderService
{
    Task<string?> ResolveAsync(
        string? prompt,
        PromptPlaceholderContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Two token families, matched in a single pass over the template so no substituted text is ever
/// re-scanned — an environment named <c>{{date}}</c>, a branch named <c>{{step:&lt;id&gt;}}</c>, or a
/// step that prints a token all reach the CLI literally:
///
///   1. Built-ins — {{datetime}}, {{date}}, {{time}}, {{env_name}}, {{git_branch}} — matched
///      case-insensitively with any default= argument ignored (a built-in always has a value).
///      Unknown names pass through untouched: they are the user-prompted variables the frontend
///      fill-values modal owns, and on headless paths they ship literally, as they always have.
///   2. {{step:&lt;guid&gt;}} — runs the referenced step hidden and captured (the same
///      TerminalScriptBuilder path as the step Test button) and splices its output in. Best-effort
///      by design: a deleted step, a disabled step, a non-zero exit, or a timeout substitutes
///      explanatory text and the launch continues — unlike lifecycle pre-steps, a broken prompt
///      step must not strand the session.
/// </summary>
public sealed partial class PromptPlaceholderService : IPromptPlaceholderService
{
    /// <summary>Substituted for a {{step:&lt;id&gt;}} whose step no longer exists.</summary>
    public const string DeletedStepText = "(user deleted this step function)";

    /// <summary>
    /// Cap per referenced step, so one runaway build log cannot eat the whole
    /// <see cref="MaxResolvedPromptChars"/> budget and fail the launch for every other token.
    /// </summary>
    public const int MaxStepOutputChars = 4000;

    /// <summary>
    /// Cap on the whole resolved message. It is typed into a shell as a single quoted argument,
    /// and Windows caps a command line around 32k chars; going over produces a mangled command
    /// rather than a clean failure, so the resolver refuses instead.
    /// </summary>
    public const int MaxResolvedPromptChars = 30_000;

    /// <summary>Substituted for a {{step:&lt;id&gt;}} whose step exists but is switched off.</summary>
    public static string DisabledStepText(string displayName) =>
        $"(step \"{displayName}\" is disabled and was not run)";

    private readonly IRepository _repository;
    private readonly ICliWrapper _cli;
    private readonly TimeProvider _timeProvider;

    public PromptPlaceholderService(
        IRepository repository,
        ICliWrapper cli,
        TimeProvider? timeProvider = null)
    {
        _repository = repository;
        _cli = cli;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<string?> ResolveAsync(
        string? prompt,
        PromptPlaceholderContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(prompt))
            return prompt;

        var resolved = prompt.Contains("{{", StringComparison.Ordinal)
            ? await ResolveTokensAsync(prompt, context, cancellationToken)
            : prompt;

        // Checked even when nothing was substituted: this is the one chokepoint every launched
        // prompt passes through, so it is the only place a length guarantee can be made.
        if (resolved.Length > MaxResolvedPromptChars)
            throw PromptTooLongException.ForResolvedPrompt(resolved.Length, MaxResolvedPromptChars);

        return resolved;
    }

    private async Task<string> ResolveTokensAsync(
        string prompt,
        PromptPlaceholderContext context,
        CancellationToken cancellationToken)
    {
        var matches = TokenRegex().Matches(prompt);
        if (matches.Count == 0)
            return prompt;

        // Everything that needs I/O is resolved up front, against the original template, so the
        // Replace pass below is pure — a value spliced in cannot become a token to resolve.
        var stepIds = new List<string>();
        var seenStepIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var needsGitBranch = false;

        foreach (Match match in matches)
        {
            if (TryReadStepId(match, out var stepId))
            {
                if (seenStepIds.Add(stepId))
                    stepIds.Add(stepId);
            }
            else if (match.Groups[1].Value.Equals("git_branch", StringComparison.OrdinalIgnoreCase))
            {
                needsGitBranch = true;
            }
        }

        // The git call costs a process spawn, so it only happens when a token asks for it.
        var gitBranch = needsGitBranch
            ? await GetGitBranchAsync(context.WorkingDirectory, cancellationToken)
            : null;

        // Each distinct step runs once even when referenced twice, sequentially in template
        // order — parallel shell commands in one working directory invite races for no win.
        var stepOutputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stepId in stepIds)
        {
            stepOutputs[stepId] = Guid.TryParse(stepId, out _)
                ? await RunStepForPromptAsync(stepId, context, cancellationToken)
                : DeletedStepText;
        }

        var now = _timeProvider.GetLocalNow();
        return TokenRegex().Replace(prompt, match =>
        {
            if (TryReadStepId(match, out var stepId))
                return stepOutputs[stepId];

            return match.Groups[1].Value.ToLowerInvariant() switch
            {
                "datetime" => now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                "date" => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "time" => now.ToString("HH:mm", CultureInfo.InvariantCulture),
                "env_name" => context.EnvironmentName ?? "",
                "git_branch" => gitBranch ?? "(no git branch)",
                // User-prompted variables are someone else's to handle — and so is {{step}} with
                // an argument that is not an id. Leave the token exactly as written.
                _ => match.Value
            };
        });
    }

    /// <summary>
    /// Recognises the {{step:&lt;id&gt;}} shape inside a generic token match and hands back the
    /// lookup key. Ids are normalised through Guid so the same step written two ways (braces,
    /// mixed case) still runs once. A syntactically valid but non-Guid id is returned as-is and
    /// resolves to the deleted-step text; anything else is not a step token at all.
    /// </summary>
    private static bool TryReadStepId(Match match, [NotNullWhen(true)] out string? stepId)
    {
        stepId = null;
        if (!match.Groups[1].Value.Equals("step", StringComparison.OrdinalIgnoreCase))
            return false;

        var argument = StepArgumentRegex().Match(match.Groups[2].Value);
        if (!argument.Success)
            return false;

        var rawId = argument.Groups[1].Value;
        stepId = Guid.TryParse(rawId, out var guid) ? guid.ToString() : rawId;
        return true;
    }

    private async Task<string> RunStepForPromptAsync(
        string stepId,
        PromptPlaceholderContext context,
        CancellationToken cancellationToken)
    {
        if (context.EnvironmentId is not int environmentId)
            return DeletedStepText;

        var step = await _repository.GetStepByIdAsync(environmentId, stepId, cancellationToken);
        if (step is null)
            return DeletedStepText;

        // Disabled is the user's "stop running this" switch — every other consumer of steps
        // honours it, and a prompt reference is no more entitled to run the command than a
        // lifecycle phase is. Distinct wording from the deleted text so the LLM (and the user
        // reading the recorded seq-1 input) can tell "turned off" from "gone".
        if (!step.Enabled)
        {
            Log.Information(
                "[Prompt] Skipping referenced step {Step} in the Initial Message — it is disabled",
                step.DisplayName);
            return DisabledStepText(step.DisplayName);
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(
            step.TimeoutSeconds, EnvironmentStep.MinTimeoutSeconds, EnvironmentStep.MaxTimeoutSeconds));

        Log.Information(
            "[Prompt] Running referenced step {Step} for the Initial Message (timeout {Timeout}s)",
            step.DisplayName, timeout.TotalSeconds);

        using var script = TerminalScriptBuilder.CreateCapturedScript(step.Command, context.WorkingDirectory);
        var result = await _cli.RunAsync(
            new CliRequest(
                script.Executable,
                script.Arguments,
                context.WorkingDirectory,
                Timeout: timeout),
            onLine: null,
            cancellationToken);

        // A cancelled run means the launch itself is being torn down — propagate rather than
        // handing back a half-truth substitution nobody will read. Thrown unconditionally: the
        // runner can cancel for reasons of its own (process teardown, a lost pipe) without our
        // token being signalled, and falling through would splice in a partial capture.
        if (result.Cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(
                $"Step \"{step.DisplayName}\" was cancelled while resolving the Initial Message.");
        }

        var output = result.StandardOutput.Trim();
        if (output.Length == 0)
            output = result.StandardError.Trim();
        output = TextTruncation.TruncateWithMarker(output, MaxStepOutputChars, "… (output truncated)");

        // A failing step's output is usually the interesting part (think `npm test`), so it is
        // kept and the failure is appended — the LLM should see both.
        if (result.TimedOut)
            return AppendNote(output, $"(step \"{step.DisplayName}\" timed out)");
        if (result.ExitCode != 0)
            return AppendNote(output, $"(step \"{step.DisplayName}\" exited with code {result.ExitCode})");

        return output;
    }

    private static string AppendNote(string output, string note) =>
        output.Length == 0 ? note : output + "\n" + note;

    private static async Task<string?> GetGitBranchAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        try
        {
            var git = new GitService(workingDirectory);
            var branch = await git.GetCurrentBranchAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(branch) ? null : branch.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "[Prompt] Could not resolve {{git_branch}} in {WorkingDirectory}", workingDirectory);
            return null;
        }
    }

    // Same shape as the frontend TOKEN_PATTERN in prompt-template-modal.js, so both sides agree
    // on what a token even is. This matches every token family — group 1 is the name, group 2 the
    // argument tail (e.g. default="..." for a user variable, :<id> for a step).
    [GeneratedRegex(@"\{\{\s*([A-Za-z_][A-Za-z0-9_.-]*)([^{}]*)\}\}")]
    private static partial Regex TokenRegex();

    // The argument tail of {{step:<guid>}} — inserted by the environment form's "Insert step
    // output" picker. The loose hex-and-dashes capture is validated with Guid.TryParse rather
    // than a stricter pattern so a malformed id degrades to the deleted-step text instead of
    // silently staying literal.
    [GeneratedRegex(@"^\s*:\s*([0-9a-fA-F\-]+)\s*$")]
    private static partial Regex StepArgumentRegex();
}
