using System.Text.RegularExpressions;

namespace VibeRails.Utils
{
    /// <summary>
    /// Filters transient terminal output (spinners, progress bars) to avoid logging redundant data
    /// </summary>
    public static class TerminalOutputFilter
    {
        private static readonly Regex AnsiRegex = new(
            @"\x1B\[[0-9;]*[a-zA-Z]",
            RegexOptions.Compiled);

        /// <summary>
        /// Detect if output is transient (spinner/progress that will be overwritten)
        /// </summary>
        /// <param name="output">Terminal output string</param>
        /// <returns>True if content is transient and should not be logged</returns>
        public static bool IsTransient(string output)
        {
            if (string.IsNullOrEmpty(output)) return true;

            // Check if output is ONLY ANSI codes + whitespace (no real content)
            var stripped = StripAnsiCodes(output);
            if (string.IsNullOrWhiteSpace(stripped))
                return true;

            return false;
        }

        /// <summary>
        /// Remove ANSI escape sequences from text
        /// </summary>
        private static string StripAnsiCodes(string input)
        {
            return AnsiRegex.Replace(input, "");
        }
    }
}
