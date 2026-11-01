using System.Text;
using System.Text.RegularExpressions;
using VibeRails.Security;

namespace VibeRails.Services.UserInOut;

/// <summary>
/// ETL filtering rules for user input before BERT embedding.
/// Pure functions — no I/O, no DI dependencies.
/// </summary>
public static class InputEtlFilter
{
    private static readonly HashSet<string> NoiseCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "ll", "la", "cd", "pwd", "clear", "cls", "exit", "quit",
        "q", "y", "n", "fg", "bg", "..", "history", "whoami", "date",
        "uptime", "top", "htop", "dir", "tree", "env", "set", "export",
        "reset", "logout", "su", "sudo", "man", "help", "ver", "version"
    };

    // --- Prompt prefix pattern ---
    //
    // Matches any leading run of prompt glyphs followed by whitespace.
    // `›` (U+203A) is Claude Code's submitted-prompt marker in its chat history view.
    // A run of `>` catches both `>>` (quoted-quote) and `>>>>>>>>> Message` echo blocks
    // that the previous single-char / `>>>`-only pattern did not strip.

    private static readonly Regex PromptPrefix = new(
        @"^[›>$%#]+\s",
        RegexOptions.Compiled);

    // These are generated launch envelopes, not a user's request. Match only the
    // beginning so a request discussing a Board prompt or command remains searchable.
    private static readonly Regex JobLaunch = new(
        "^(?:exec\\s+|&\\s*)?(?:\"[^\"]*[/\\\\]vb(?:\\.exe)?\"|'[^']*[/\\\\]vb(?:\\.exe)?'|(?:[^\\s\"']*[/\\\\])?vb(?:\\.exe)?)(?=\\s).*?(?<![\\w-])--(?:job-run|run-python-script)(?=[\\s\"'])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Since VIBE-153 every value in a generated PowerShell command is a base64 literal, so the
    // Automation flag is visible only after decoding (VIBE-257).
    private static readonly Regex EncodedPowerShellValue = new(
        @"\[System\.Convert\]::FromBase64String\('([A-Za-z0-9+/]{1,4096}={0,2})'\)",
        RegexOptions.Compiled);

    private const string DiscussionHeader = "The user wants to talk with you about kanban card ";
    private const string DiscussionQuestion = "User's initial question (data): ";
    private const string DiscussionQuestionEnd = " Answer the question, then wait.";

    private static readonly string[] BoardLaunchPrefixes =
    [
        "You are working on kanban card ",
        "You are managing kanban card ",
        "You are the manager agent for this card's Agent workflow. ",
        "This is a delegated worker in an Agent workflow. ",
        "You are the code review agent. Purpose: Code review. ",
        "For Jira-linked cards, Board comments sync to Jira by default. ",
        "For Azure DevOps-linked cards, Board comments post to the work item by default. "
    ];

    /// <summary>
    /// Full ETL pipeline. Returns cleaned text ready for embedding,
    /// or null if the input should be skipped entirely.
    /// </summary>
    public static string? Process(string? rawInput)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
            return null;

        var normalized = Normalize(rawInput);
        if (string.IsNullOrWhiteSpace(normalized))
            return null;

        if (IsGeneratedLaunchPrompt(normalized)
            || JobLaunch.IsMatch(normalized)
            || IsGeneratedPowerShellLaunch(normalized))
            return null;

        // A card discussion bootstrap is generated; only the question typed into it is the user's.
        if (UserQuestionInDiscussion(normalized) is { } question)
        {
            if (question.Length == 0) return null;
            normalized = question;
        }

        if (ContainsSecret(normalized))
            return null;

        if (IsNoise(normalized))
            return null;

        return normalized;
    }

    private static bool IsGeneratedPowerShellLaunch(string text)
    {
        if (!text.StartsWith("[Console]::OutputEncoding = ", StringComparison.Ordinal)) return false;
        if (text.Contains("--job-run", StringComparison.Ordinal)) return true;
        foreach (var value in EncodedPowerShellValue.Matches(text).Take(64))
        {
            try
            {
                if (Encoding.UTF8.GetString(Convert.FromBase64String(value.Groups[1].Value)) is "--job-run" or "--run-python-script")
                    return true;
            }
            catch (FormatException) { }
        }
        return false;
    }

    /// <summary>
    /// Null when <paramref name="text"/> is not a card discussion bootstrap; otherwise the user's
    /// question from it, or empty when the discussion was opened without one.
    /// </summary>
    private static string? UserQuestionInDiscussion(string text)
    {
        var start = text.StartsWith(DiscussionHeader, StringComparison.Ordinal) ? 0
            : BoardLaunchPrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal))
                ? text.IndexOf(DiscussionHeader, StringComparison.Ordinal) : -1;
        // Every bootstrap version, with or without a question, ends by declaring the discussion.
        if (start < 0 || text.IndexOf("This is a discussion session.", start, StringComparison.Ordinal) < 0) return null;
        var question = text.IndexOf(DiscussionQuestion, start, StringComparison.Ordinal);
        if (question < 0) return "";
        question += DiscussionQuestion.Length;
        var end = text.LastIndexOf(DiscussionQuestionEnd, StringComparison.Ordinal);
        return (end > question ? text[question..end] : text[question..]).Trim();
    }

    private static bool IsGeneratedLaunchPrompt(string text)
    {
        if (text.StartsWith("This is an Automation Worker session. ", StringComparison.Ordinal)) return true;
        // Older builds started Automation prompts with the card header itself.
        if (text.StartsWith("This Automation was triggered for kanban card ", StringComparison.Ordinal)
            && text.Contains(". The user has authorized the viberails-mcp Board tools for this card session. ", StringComparison.Ordinal))
            return true;
        // Review, manager, delegated and remotely linked launches prepend guidance
        // before the card header. Require both that prefix and the generated header.
        return (BoardLaunchPrefixes.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal))
                || (text.StartsWith("Purpose: ", StringComparison.Ordinal)
                    && text.Contains(". Expected output: the configured Worker output and a card handoff. ", StringComparison.Ordinal)))
            && (text.Contains("You are working on kanban card ", StringComparison.Ordinal)
                || text.Contains("You are managing kanban card ", StringComparison.Ordinal)
                || text.Contains("This Automation was triggered for kanban card ", StringComparison.Ordinal));
    }

    /// <summary>Detects possible secrets using the shared search-index policy.</summary>
    public static bool ContainsSecret(string text) => CredentialRedactor.ContainsKnownSecret(text);

    /// <summary>
    /// Returns true if the text is a short noise command with no semantic value.
    /// </summary>
    public static bool IsNoise(string text)
    {
        if (text.Length <= 2)
            return true;

        // Pure numeric input (menu selections)
        if (text.All(c => char.IsDigit(c)))
            return true;

        // Single non-alphanumeric character
        if (text.Length == 1 && !char.IsLetterOrDigit(text[0]))
            return true;

        // Known noise commands (exact match after trim)
        if (NoiseCommands.Contains(text))
            return true;

        // Commands with simple arguments: "cd ..", "cd /tmp", "ls -la"
        var firstSpace = text.IndexOf(' ');
        if (firstSpace > 0)
        {
            var command = text[..firstSpace];
            if (command is "cd" or "ls" or "dir" or "ll" or "la")
                return true;
        }

        return false;
    }

    /// <summary>
    /// Normalizes input text: strips prompt prefixes, normalizes whitespace,
    /// removes control characters.
    /// </summary>
    public static string Normalize(string text)
    {
        // Remove null bytes
        var result = text.Replace("\0", string.Empty);

        // Normalize newlines
        result = result.Replace("\r\n", "\n").Replace('\r', '\n');

        // Strip prompt prefixes (repeatedly, in case of nested like "> > ")
        result = result.Trim();
        while (PromptPrefix.IsMatch(result))
        {
            result = PromptPrefix.Replace(result, string.Empty, 1).TrimStart();
        }

        // Collapse internal whitespace runs to single space
        result = CollapseWhitespace(result);

        return result.Trim();
    }

    private static string CollapseWhitespace(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var sb = new System.Text.StringBuilder(value.Length);
        var prevWasSpace = false;
        foreach (var c in value)
        {
            if (c is ' ' or '\t' or '\n')
            {
                if (!prevWasSpace)
                {
                    sb.Append(' ');
                    prevWasSpace = true;
                }
            }
            else
            {
                sb.Append(c);
                prevWasSpace = false;
            }
        }
        return sb.ToString();
    }
}
