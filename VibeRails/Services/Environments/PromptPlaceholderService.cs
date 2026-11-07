using System.Globalization;
using System.Text.RegularExpressions;
using Serilog;
using VibeRails.DTOs;
using VibeRails.Services.Workspaces;
using VibeRails.Utils;

namespace VibeRails.Services.Environments;

/// <summary>
/// Execution context for ordinary placeholders. EnvironmentId is retained only for source compatibility.
/// </summary>
public sealed record PromptPlaceholderContext(
    string WorkingDirectory,
    int? EnvironmentId = null,
    string? EnvironmentName = null,
    string? BoardCardKey = null);

/// <summary>The once-resolved message and the directory the CLI must use.</summary>
public sealed record ResolvedPrompt(string? Prompt, string WorkingDirectory);

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

    /// <summary>The resolved message itself is over budget — including substituted context.</summary>
    public static PromptTooLongException ForResolvedPrompt(int actualChars, int maxChars) => new(
        $"The Initial Message resolved to {actualChars:N0} characters, over the {maxChars:N0} character limit. " +
        "Shorten the initial message.",
        actualChars, maxChars);

    /// <summary>A resumed session's recap plus its attached Board card references is over budget.</summary>
    public static PromptTooLongException ForResumeContext(int actualChars, int maxChars, int cardCount) => new(
        $"The resume recap plus its {cardCount:N0} attached Board card references total {actualChars:N0} characters, " +
        $"over the {maxChars:N0} character limit. Shorten the recap, or detach cards from the source session.",
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
/// Resolves supported placeholders exactly once in the process owning the PTY.
/// Retired executable step syntax is rejected before any worktree or process side effect.
/// </summary>
public interface IPromptPlaceholderService
{
    Task<ResolvedPrompt> ResolveLaunchAsync(
        string? prompt,
        PromptPlaceholderContext context,
        CancellationToken cancellationToken = default);

    Task<string?> ResolveAsync(
        string? prompt,
        PromptPlaceholderContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolves supported built-ins once; substituted values are never rescanned.</summary>
public sealed partial class PromptPlaceholderService : IPromptPlaceholderService
{
    public const int MaxResolvedPromptChars = 30_000;
    private readonly TimeProvider _timeProvider;
    private readonly IManagedWorktreeService? _worktrees;

    public PromptPlaceholderService(TimeProvider? timeProvider = null, IManagedWorktreeService? worktrees = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _worktrees = worktrees;
    }

    public async Task<string?> ResolveAsync(
        string? prompt,
        PromptPlaceholderContext context,
        CancellationToken cancellationToken = default) =>
        (await ResolveLaunchAsync(prompt, context, cancellationToken)).Prompt;

    public async Task<ResolvedPrompt> ResolveLaunchAsync(
        string? prompt,
        PromptPlaceholderContext context,
        CancellationToken cancellationToken = default)
    {
        VibeRails.Launches.LaunchValidation.RejectRetiredPrompt(prompt);
        if (string.IsNullOrEmpty(prompt))
            return new ResolvedPrompt(prompt, context.WorkingDirectory);

        var resolved = prompt.Contains("{{", StringComparison.Ordinal)
            ? await ResolveTokensAsync(prompt, context, cancellationToken)
            : new ResolvedPrompt(prompt, context.WorkingDirectory);

        // Checked even when nothing was substituted: this is the one chokepoint every launched
        // prompt passes through, so it is the only place a length guarantee can be made.
        if (resolved.Prompt!.Length > MaxResolvedPromptChars)
            throw PromptTooLongException.ForResolvedPrompt(resolved.Prompt.Length, MaxResolvedPromptChars);

        return resolved;
    }

    private async Task<ResolvedPrompt> ResolveTokensAsync(
        string prompt,
        PromptPlaceholderContext context,
        CancellationToken cancellationToken)
    {
        var matches = TokenRegex().Matches(prompt);
        if (matches.Count == 0)
            return new ResolvedPrompt(prompt, context.WorkingDirectory);

        // Validate every worktree reference before creating anything. Repeated references share
        // one checkout; conflicting options cannot select two working directories for one CLI.
        var options = matches.Cast<Match>()
            .Where(match => match.Groups[1].Value.Equals("worktree", StringComparison.OrdinalIgnoreCase))
            .Select(match => WorktreeOptions.Parse(match.Groups[2].Value)).Distinct().ToArray();
        if (options.Length > 1) throw new ArgumentException("An Initial Message can select only one set of {{worktree}} options.");
        string? worktreePath = null;
        if (options.Length == 1)
        {
            var service = _worktrees ?? throw new InvalidOperationException("Worktree support is unavailable in this host.");
            var worktree = await service.CreateAsync(context.WorkingDirectory, options[0], cancellationToken);
            worktreePath = worktree.Path;
            context = context with { WorkingDirectory = worktree.Path };
        }

        var needsGitBranch = matches.Cast<Match>().Any(match =>
            match.Groups[1].Value.Equals("git_branch", StringComparison.OrdinalIgnoreCase));
        var gitBranch = needsGitBranch ? await GetGitBranchAsync(context.WorkingDirectory, cancellationToken) : null;
        var now = _timeProvider.GetLocalNow();
        var resolved = TokenRegex().Replace(prompt, match =>
        {
            return match.Groups[1].Value.ToLowerInvariant() switch
            {
                "datetime" => now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                "date" => now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "time" => now.ToString("HH:mm", CultureInfo.InvariantCulture),
                "env_name" => context.EnvironmentName ?? "",
                "git_branch" => gitBranch ?? "(no git branch)",
                "board_card" => context.BoardCardKey ?? "",
                "worktree" => worktreePath!,
                // User-prompted variables retain their existing caller-owned handling.
                _ => match.Value
            };
        });
        return new ResolvedPrompt(resolved, context.WorkingDirectory);
    }

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

}
