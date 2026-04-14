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
        /// Detect if output is just spinner/bullet noise (• ◦) that shouldn't reset idle timers.
        /// Small output consisting only of bullet chars, whitespace, and ANSI escapes is noise.
        /// </summary>
        public static bool IsSpinnerNoise(string output)
        {
            if (string.IsNullOrEmpty(output)) return false;
            if (output.Length > 64) return false;

            var stripped = StripAnsiCodes(output);
            if (string.IsNullOrWhiteSpace(stripped))
                return false; // pure ANSI/whitespace handled by IsTransient

            bool hasBullet = false;
            foreach (var ch in stripped)
            {
                if (ch is '\u2022' or '\u25E6') // • ◦
                {
                    hasBullet = true;
                    continue;
                }
                if (char.IsWhiteSpace(ch))
                    continue;

                return false; // non-bullet visible char = real output
            }

            return hasBullet;
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
