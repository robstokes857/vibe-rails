using System.Text.RegularExpressions;
using System.Text;

namespace VibeRails.Utils;

/// <summary>
/// Validates and escapes CLI arguments to prevent shell command injection.
/// CustomArgs are intended to be flags/values passed to LLM CLIs (claude, codex, gemini).
/// </summary>
public static partial class ShellArgSanitizer
{
    /// <summary>
    /// Characters that cannot appear in CLI arguments under any circumstances.
    /// Every other shell metacharacter (<c>; | &amp; $ ` { } &lt; &gt; ! ( ) *</c>) is
    /// neutralized at emit time by <see cref="EscapeArg"/>, which always wraps args in
    /// single quotes (POSIX) or backtick-escaped double quotes (PowerShell). Inside
    /// those quotes those metacharacters are literal, so blocking them here would
    /// just reject legitimate prompts (<c>--system-prompt "Use $HOME!"</c>) without
    /// adding any safety. The chars below are different: they cannot survive our
    /// per-arg quoting (NUL terminates strings; CR/LF break command lines and our
    /// own argument tokenizer treats them as whitespace separators).
    /// </summary>
    private static readonly char[] DangerousChars = ['\0', '\n', '\r'];

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
                       "Only CLI flags and values are allowed (e.g. \"--permission-mode plan\").";
            }
        }

        try
        {
            _ = ParseArguments(customArgs);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
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

        return ParseArguments(customArgs);
    }

    private static string[] ParseArguments(string customArgs)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuote = false;
        var quoteChar = '\0';
        var escaping = false;
        var hasToken = false;

        foreach (var ch in customArgs)
        {
            if (escaping)
            {
                current.Append(ch);
                escaping = false;
                hasToken = true;
                continue;
            }

            if (inQuote)
            {
                if (ch == '\\')
                {
                    escaping = true;
                    continue;
                }

                if (ch == quoteChar)
                {
                    inQuote = false;
                    continue;
                }

                current.Append(ch);
                hasToken = true;
                continue;
            }

            if (ch == '"' || ch == '\'')
            {
                inQuote = true;
                quoteChar = ch;
                hasToken = true;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (hasToken)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
                continue;
            }

            current.Append(ch);
            hasToken = true;
        }

        if (escaping)
        {
            current.Append('\\');
        }

        if (inQuote)
        {
            throw new ArgumentException("CustomArgs contains an unterminated quoted value.");
        }

        if (hasToken)
        {
            args.Add(current.ToString());
        }

        return args.ToArray();
    }

    /// <summary>
    /// Wraps a value in POSIX single quotes, escaping embedded single quotes
    /// (<c>' → '\''</c>). The canonical POSIX shell-quoting primitive — call this
    /// instead of re-implementing it (see MacTerminalCommandBuilder).
    /// </summary>
    public static string QuotePosixSingleQuoted(string value)
        => "'" + value.Replace("'", "'\\''") + "'";

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
            var escaped = arg
                .Replace("`", "``")
                .Replace("\"", "`\"")
                .Replace("$", "`$");
            return "\"" + escaped + "\"";
        }

        return QuotePosixSingleQuoted(arg);
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
