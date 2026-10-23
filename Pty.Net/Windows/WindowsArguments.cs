// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

namespace Pty.Net.Windows
{
    using System.Linq;
    using System.Text;

    /// <summary>
    /// Helper class for formatting windows arguments when passing them to conpty.
    /// </summary>
    internal static class WindowsArguments
    {
        /// <summary>
        /// Quotes each argument before joining together.
        /// </summary>
        /// <param name="args">The command line arguments to format.</param>
        /// <returns>a space-delimited list of command line arguments that <c>CommandLineToArgvW</c> splits back into <paramref name="args"/>.</returns>
        public static string Format(params string[]? args) =>
            string.Join(" ", (args ?? Enumerable.Empty<string>()).Select(QuoteArgument));

        /// <summary>
        /// Joins the arguments together witout modification.
        /// </summary>
        /// <param name="args">The command line arguments to format.</param>
        /// <returns>A space-delimited list of command line arguments.</returns>
        public static string FormatVerbatim(params string[]? args) =>
            string.Join(" ", args ?? Enumerable.Empty<string>());

        /// <summary>
        /// Quotes a single argument so the Win32 command-line parser (<c>CommandLineToArgvW</c>)
        /// round-trips it back to the original value: backslashes are doubled only where they
        /// precede a quote, and an empty argument stays an argument. Keep this identical to
        /// <c>VibeRails.Services.VCA.Hooks.VcaHookProcessLaunch.QuoteArgument</c>; this project
        /// cannot reference VibeRails, and a test pins the two together.
        /// </summary>
        /// <param name="value">One argument.</param>
        /// <returns>The argument, quoted when it contains whitespace or a quote.</returns>
        internal static string QuoteArgument(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "\"\"";
            }

            var needsQuotes = value.Contains(' ') || value.Contains('\t') || value.Contains('"');
            if (!needsQuotes)
            {
                return value;
            }

            var sb = new StringBuilder();
            sb.Append('"');
            for (var i = 0; i < value.Length; i++)
            {
                var backslashes = 0;
                while (i < value.Length && value[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i == value.Length)
                {
                    sb.Append('\\', backslashes * 2);
                }
                else if (value[i] == '"')
                {
                    sb.Append('\\', (backslashes * 2) + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(value[i]);
                }
            }

            sb.Append('"');
            return sb.ToString();
        }
    }
}
