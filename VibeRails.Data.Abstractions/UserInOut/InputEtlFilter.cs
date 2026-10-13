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
