using System.Text.RegularExpressions;

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

    // --- Secret detection patterns (compiled once) ---

    private static readonly Regex[] SecretPatterns =
    [
        // Anthropic API keys
        new(@"sk-ant-api\d{2}-[A-Za-z0-9_-]{20,}", RegexOptions.Compiled),

        // OpenAI API keys
        new(@"sk-[A-Za-z0-9]{20,}", RegexOptions.Compiled),

        // AWS access key IDs
        new(@"AKIA[A-Z0-9]{16}", RegexOptions.Compiled),

        // GitHub tokens (classic and fine-grained)
        new(@"gh[pos]_[A-Za-z0-9]{36,}", RegexOptions.Compiled),
        new(@"github_pat_[A-Za-z0-9_]{22,}", RegexOptions.Compiled),

        // Slack tokens
        new(@"xox[bpsar]-[A-Za-z0-9\-]{10,}", RegexOptions.Compiled),

        // Environment variable assignments setting secret-named variables. Leading
        // word boundary keeps the `setx?` branch from matching the "set" tail inside
        // ordinary words like "reset" — without it any sentence containing both
        // "reset" and a secret-named keyword (e.g. "How do I reset my password?")
        // gets falsely flagged.
        new(@"\b(?:export|setx?|\[Environment\].*SetEnvironmentVariable)\b.*\b(?:KEY|TOKEN|SECRET|PASSWORD|CREDENTIAL)\b",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // PowerShell SetEnvironmentVariable with quoted args (catches the specific pattern from the user's example)
        new(@"\[Environment\]::SetEnvironmentVariable\s*\(",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),

        // Inline credential assignments: password=xyz, token: abc123etc
        new(@"(?:password|token|secret|api_?key|credential|auth)\s*[=:]\s*\S{8,}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase),
    ];

    // --- Prompt prefix pattern ---
    //
    // Matches any leading run of prompt glyphs followed by whitespace.
    // `›` (U+203A) is Claude Code's submitted-prompt marker in its chat history view.
    // A run of `>` catches both `>>` (quoted-quote) and `>>>>>>>>> Message` echo blocks
    // that the previous single-char / `>>>`-only pattern did not strip.

    private static readonly Regex PromptPrefix = new(
        @"^[›>$%#]+\s",
        RegexOptions.Compiled);

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

        if (ContainsSecret(normalized))
            return null;

        if (IsNoise(normalized))
            return null;

        return normalized;
    }

    /// <summary>
    /// Returns true if the text appears to contain secrets or credentials.
    /// </summary>
    public static bool ContainsSecret(string text)
    {
        foreach (var pattern in SecretPatterns)
        {
            if (pattern.IsMatch(text))
                return true;
        }
        return false;
    }

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
