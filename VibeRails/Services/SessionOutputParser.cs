using System.Text;
using TerminalEmulator;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using EmulatorTerminal = TerminalEmulator.Terminal;
using VibeTerminal = VibeRails.Services.Terminal.Terminal;

namespace VibeRails.Services;

public sealed class SessionOutputParser : ISessionOutputParser
{
    private const int ScrollbackSize = 20000;
    private const int RecentLineWindow = 32;
    private const string UiOnlyChars = " -_=~|:<>[](){}./\\`'\"─━│┃┌┐└┘├┤┬┴┼╭╮╰╯╞╡╪╫╬═║╒╓╔╕╖╗╘╙╚╛╜╝▀▄▁▂▃▅▆▇█▉▊▋▌▍▎▏▐▔▕▖▗▘▙▚▛▜▝▞▟";

    public Task<string> ParseAsync(IReadOnlyList<SessionLogChunkRecord> chunks, CancellationToken cancellationToken = default)
    {
        var terminal = new EmulatorTerminal(
            cols: VibeTerminal.DefaultCols,
            rows: VibeTerminal.DefaultRows,
            scrollbackSize: ScrollbackSize);

        var alternateLines = new List<string>();
        var previousAlternateScreen = Array.Empty<string>();
        var sawAlternateScreen = false;
        var wasAlternateScreen = false;

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (chunk.Content.Length == 0)
                continue;

            terminal.Write(chunk.Content.AsSpan());

            if (terminal.IsAlternateScreen)
            {
                sawAlternateScreen = true;
                if (!wasAlternateScreen)
                    previousAlternateScreen = Array.Empty<string>();

                AppendStableAlternateRows(terminal, previousAlternateScreen, alternateLines);
                previousAlternateScreen = terminal.GetScreenText();
            }
            else if (wasAlternateScreen)
            {
                previousAlternateScreen = Array.Empty<string>();
            }

            wasAlternateScreen = terminal.IsAlternateScreen;
        }

        if (sawAlternateScreen)
        {
            foreach (var line in terminal.GetScreenText())
                alternateLines.Add(line);
        }

        var candidateLines = sawAlternateScreen
            ? alternateLines
            : GetNormalScreenLines(terminal);

        return Task.FromResult(BuildOutputText(candidateLines));
    }

    private static List<string> GetNormalScreenLines(EmulatorTerminal terminal)
    {
        var lines = new List<string>();

        foreach (var row in terminal.GetScrollback())
            lines.Add(ConvertRowToText(row));

        lines.AddRange(terminal.GetScreenText());
        return lines;
    }

    private static string ConvertRowToText(TerminalCell[] row)
    {
        var builder = new StringBuilder(row.Length);
        foreach (var cell in row)
            cell.AppendText(builder, replaceControlWithSpace: true);

        return builder.ToString().TrimEnd();
    }

    private static void AppendStableAlternateRows(
        EmulatorTerminal terminal,
        IReadOnlyList<string> previousScreen,
        ICollection<string> output)
    {
        var currentScreen = terminal.GetScreenText();
        var stableRowCount = terminal.CursorCol == 0
            ? terminal.CursorRow
            : Math.Max(terminal.CursorRow, 0);

        stableRowCount = Math.Min(stableRowCount, currentScreen.Length);

        for (var row = 0; row < stableRowCount; row++)
        {
            var currentLine = currentScreen[row];
            var previousLine = row < previousScreen.Count ? previousScreen[row] : string.Empty;

            if (string.Equals(currentLine, previousLine, StringComparison.Ordinal))
                continue;

            if (string.IsNullOrWhiteSpace(currentLine))
                continue;

            output.Add(currentLine);
        }
    }

    private static string BuildOutputText(IEnumerable<string> rawLines)
    {
        var output = new List<string>();
        var recentCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var recentQueue = new Queue<string>();
        var pendingBlankLine = false;

        foreach (var rawLine in rawLines)
        {
            var line = NormalizeLine(rawLine);
            if (line.Length == 0)
            {
                pendingBlankLine = output.Count > 0;
                continue;
            }

            if (ShouldSkipLine(line))
                continue;

            line = StripLeadMarker(line);
            if (line.Length == 0 || ShouldSkipLine(line))
                continue;

            if (recentCounts.ContainsKey(line))
                continue;

            if (pendingBlankLine && output.Count > 0 && output[^1].Length > 0)
                output.Add(string.Empty);

            pendingBlankLine = false;
            output.Add(line);
            RememberRecentLine(line, recentQueue, recentCounts);
        }

        while (output.Count > 0 && output[^1].Length == 0)
            output.RemoveAt(output.Count - 1);

        return string.Join("\n", output);
    }

    private static string NormalizeLine(string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
            return string.Empty;

        return rawLine
            .Replace('\u00A0', ' ')
            .Replace("\u200B", string.Empty, StringComparison.Ordinal)
            .Replace("\u200C", string.Empty, StringComparison.Ordinal)
            .Replace("\u200D", string.Empty, StringComparison.Ordinal)
            .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static string StripLeadMarker(string line)
    {
        if (line.Length < 2)
            return line;

        if ((line[0] is '•' or '✦' or '●') && char.IsWhiteSpace(line[1]))
            return line[1..].TrimStart();

        return line;
    }

    private static void RememberRecentLine(
        string line,
        Queue<string> recentQueue,
        Dictionary<string, int> recentCounts)
    {
        recentQueue.Enqueue(line);
        recentCounts.TryGetValue(line, out var count);
        recentCounts[line] = count + 1;

        while (recentQueue.Count > RecentLineWindow)
        {
            var removed = recentQueue.Dequeue();
            if (!recentCounts.TryGetValue(removed, out var removedCount))
                continue;

            if (removedCount <= 1)
                recentCounts.Remove(removed);
            else
                recentCounts[removed] = removedCount - 1;
        }
    }

    private static bool ShouldSkipLine(string line)
    {
        return IsUiDecoration(line)
            || IsShellNoise(line)
            || IsUserPrompt(line)
            || IsCliChrome(line)
            || IsToolNoise(line)
            || IsProgressNoise(line);
    }

    private static bool IsUiDecoration(string line)
    {
        foreach (var ch in line)
        {
            if (!char.IsWhiteSpace(ch) && !UiOnlyChars.Contains(ch))
                return false;
        }

        return true;
    }

    private static bool IsShellNoise(string line)
    {
        if (line.StartsWith("PowerShell ", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("Loading personal and system profiles took", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("(base) PS ", StringComparison.OrdinalIgnoreCase) || line.StartsWith("PS ", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("Added global MCP server", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("MCP server \"", StringComparison.OrdinalIgnoreCase)
            && ContainsAny(line, "configured", "updated"))
        {
            return true;
        }

        return ContainsAny(
            line,
            "> claude",
            "> codex",
            "> gemini",
            "> copilot",
            "$ claude",
            "$ codex",
            "$ gemini",
            "$ copilot");
    }

    private static bool IsUserPrompt(string line)
    {
        return line.StartsWith("› ", StringComparison.Ordinal)
            || line.StartsWith("> ", StringComparison.Ordinal)
            || string.Equals(line, ">", StringComparison.Ordinal);
    }

    private static bool IsCliChrome(string line)
    {
        if (line.Equals("Claude Code", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("model:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("directory:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Tip: ", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("? for shortcuts", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (line.Contains("Sonnet ", StringComparison.OrdinalIgnoreCase)
            && line.Contains("Claude Pro", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return ContainsAny(
            line,
            "Claude Code v",
            "OpenAI Codex (v",
            "Gemini CLI v",
            "Gemini Code Assist",
            "Logged in with Google /auth",
            "Type your message or @path/to/file",
            "accept edits on (shift+tab to cycle)",
            "shift+tab to accept edits",
            "no sandbox (see /docs)",
            " /model ",
            "/model to change",
            "% left",
            " /effort",
            "MCP startup incomplete",
            "MCP client for `",
            "MCP issues detected",
            "weekly limit left",
            "GEMINI.md file | ",
            " /mcp");
    }

    private static bool IsToolNoise(string line)
    {
        if (line.StartsWith("⎿", StringComparison.Ordinal))
            return true;

        if (line.StartsWith("● Update(", StringComparison.Ordinal)
            || line.StartsWith("● Read(", StringComparison.Ordinal)
            || line.StartsWith("● Search(", StringComparison.Ordinal)
            || line.StartsWith("● Bash(", StringComparison.Ordinal)
            || line.StartsWith("● Edit(", StringComparison.Ordinal)
            || line.StartsWith("● Write(", StringComparison.Ordinal))
        {
            return true;
        }

        if (line.StartsWith("Determining projects to restore", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("All projects are up-to-date", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("Test run for ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return LooksLikeNumberedDiffLine(line);
    }

    private static bool IsProgressNoise(string line)
    {
        if (line.Length == 0)
            return false;

        if (line[0] is '✻' or '◐' or '◑' or '◒' or '◓')
            return true;

        if (line.StartsWith("⚠ ", StringComparison.Ordinal) || line.StartsWith("ℹ ", StringComparison.Ordinal))
            return true;

        return false;
    }

    private static bool LooksLikeNumberedDiffLine(string line)
    {
        var index = 0;
        while (index < line.Length && char.IsDigit(line[index]))
            index++;

        if (index == 0 || index >= line.Length)
            return false;

        while (index < line.Length && char.IsWhiteSpace(line[index]))
            index++;

        return index < line.Length && (line[index] == '+' || line[index] == '-');
    }

    private static bool ContainsAny(string text, params string[] values)
    {
        foreach (var value in values)
        {
            if (text.Contains(value, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
