using System.Text.RegularExpressions;

namespace VibeRails.Utils;

/// <summary>
/// Validates and escapes CLI arguments to prevent shell command injection.
/// CustomArgs are intended to be flags/values passed to LLM CLIs (claude, codex, gemini).
/// </summary>
public static partial class ShellArgSanitizer
{
    /// <summary>
    /// Shell metacharacters that must never appear in CLI arguments.
    /// These enable command chaining, piping, subshell execution, and redirection.
    /// </summary>
    private static readonly char[] DangerousChars =
        [';', '|', '&', '$', '`', '(', ')', '{', '}', '<', '>', '!', '\n', '\r', '\0'];

    [GeneratedRegex(@"^--?[a-zA-Z][a-zA-Z0-9_-]*$")]
    private static partial Regex FlagPattern();

    /// <summary>
    /// Validates a CustomArgs string (space-separated CLI args) for shell safety.
    /// Returns null if valid, or an error message describing the problem.
    /// </summary>
    public static string? Validate(string? customArgs)
    {
        if (string.IsNullOrWhiteSpace(customArgs))
            return null; // empty is fine

        foreach (var ch in customArgs)
        {
            if (Array.IndexOf(DangerousChars, ch) >= 0)
            {
                var display = ch switch
                {
                    '\n' => "\\n",
                    '\r' => "\\r",
                    '\0' => "\\0",
                    _ => ch.ToString()
                };
                return $"CustomArgs contains forbidden character '{display}'. " +
                       "Only CLI flags and values are allowed (e.g. \"--model opus\").";
            }
        }

        return null;
    }

    /// <summary>
    /// Splits a CustomArgs string into individual arguments and validates each one.
    /// Returns the parsed args array, or throws ArgumentException if invalid.
    /// </summary>
    public static string[] ParseAndValidate(string? customArgs)
    {
        if (string.IsNullOrWhiteSpace(customArgs))
            return [];

        var error = Validate(customArgs);
        if (error != null)
            throw new ArgumentException(error);

        return customArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Escapes a single argument for safe inclusion in a shell command string.
    /// Wraps in single quotes (Unix) or double quotes (Windows) with proper internal escaping.
    /// </summary>
    public static string EscapeArg(string arg)
    {
        if (string.IsNullOrEmpty(arg))
            return "\"\"";

        if (OperatingSystem.IsWindows())
        {
            // Windows cmd/PowerShell: wrap in double quotes, escape internal double quotes
            return "\"" + arg.Replace("\"", "\\\"") + "\"";
        }

        // Unix shells: wrap in single quotes, escape internal single quotes
        // In single quotes, the only character that needs escaping is ' itself
        // Done by ending the single-quoted string, adding an escaped quote, and restarting
        return "'" + arg.Replace("'", "'\\''") + "'";
    }

    /// <summary>
    /// Takes validated args (already split) and produces a safe command fragment.
    /// Each arg is individually escaped for shell safety.
    /// </summary>
    public static string BuildSafeArgString(string[] args)
    {
        if (args.Length == 0)
            return "";

        return string.Join(" ", args.Select(EscapeArg));
    }
}
