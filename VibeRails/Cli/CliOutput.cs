namespace VibeRails.Cli
{
    /// <summary>
    /// Utilities for consistent CLI output formatting
    /// </summary>
    public static class CliOutput
    {
        public static void Success(string message)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(message);
            Console.ResetColor();
        }

        public static void Error(string message)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine($"Error: {message}");
            Console.ResetColor();
        }

        public static void Warning(string message)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"Warning: {message}");
            Console.ResetColor();
        }

        public static void Info(string message)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(message);
            Console.ResetColor();
        }

        public static void Line(string message = "")
        {
            Console.WriteLine(message);
        }

        /// <summary>
        /// Prints a simple table with headers and rows
        /// </summary>
        public static void Table(string[] headers, List<string[]> rows)
        {
            if (headers.Length == 0) return;

            // Calculate column widths
            var widths = new int[headers.Length];
            for (int i = 0; i < headers.Length; i++)
            {
                widths[i] = headers[i].Length;
            }

            foreach (var row in rows)
            {
                for (int i = 0; i < Math.Min(row.Length, widths.Length); i++)
                {
                    widths[i] = Math.Max(widths[i], (row[i] ?? "").Length);
                }
            }

            // Print header
            Console.ForegroundColor = ConsoleColor.White;
            for (int i = 0; i < headers.Length; i++)
            {
                Console.Write(headers[i].PadRight(widths[i] + 2));
            }
            Console.WriteLine();

            // Print separator
            Console.ForegroundColor = ConsoleColor.DarkGray;
            for (int i = 0; i < headers.Length; i++)
            {
                Console.Write(new string('-', widths[i]) + "  ");
            }
            Console.WriteLine();
            Console.ResetColor();

            // Print rows
            foreach (var row in rows)
            {
                for (int i = 0; i < headers.Length; i++)
                {
                    var value = i < row.Length ? (row[i] ?? "") : "";
                    Console.Write(value.PadRight(widths[i] + 2));
                }
                Console.WriteLine();
            }
        }

        /// <summary>
        /// Prints a key-value detail view
        /// </summary>
        public static void Detail(string label, string value)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write($"{label}: ");
            Console.ResetColor();
            Console.WriteLine(value);
        }

        /// <summary>
        /// Prints an enforcement level with appropriate color
        /// </summary>
        public static void EnforcementBadge(string level)
        {
            var color = level.ToUpperInvariant() switch
            {
                "STOP" => ConsoleColor.Red,
                "COMMIT" => ConsoleColor.Yellow,
                "WARN" => ConsoleColor.Blue,
                _ => ConsoleColor.Gray
            };

            Console.ForegroundColor = color;
            Console.Write($"[{level}]");
            Console.ResetColor();
        }

        public static void Help(string usage, string description, Dictionary<string, string> commands, Dictionary<string, string>? options = null)
        {
            Console.WriteLine(description);
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("Usage:");
            Console.ResetColor();
            Console.WriteLine($"  {usage}");
            Console.WriteLine();

            if (commands.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("Commands:");
                Console.ResetColor();

                var maxLen = commands.Keys.Max(k => k.Length);
                foreach (var (cmd, desc) in commands)
                {
                    Console.Write($"  {cmd.PadRight(maxLen + 2)}");
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine(desc);
                    Console.ResetColor();
                }
                Console.WriteLine();
            }

            if (options != null && options.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine("Options:");
                Console.ResetColor();

                var maxLen = options.Keys.Max(k => k.Length);
                foreach (var (opt, desc) in options)
                {
                    Console.Write($"  {opt.PadRight(maxLen + 2)}");
                    Console.ForegroundColor = ConsoleColor.Gray;
                    Console.WriteLine(desc);
                    Console.ResetColor();
                }
            }
        }
    }
}
