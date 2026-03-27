using System.Text;
using TerminalEmulator;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using EmulatorTerminal = TerminalEmulator.Terminal;
using VibeTerminal = VibeRails.Services.Terminal.Terminal;

namespace VibeRails.Services;

/// <summary>
/// Replays raw PTY bytes through the terminal emulator, then extracts a readable transcript
/// from the final rendered lines. When a structured chat layout is not detected, falls back
/// to V1-style noise filtering over the stable rendered output.
/// </summary>
public sealed class SessionParseV3 : ISessionOutputParser
{
    private const int ScrollbackSize = 20_000;
    private const int RecentLineWindow = 32;
    private const string UiOnlyChars = " -_=~|:<>[](){}./\\`'\"─━│┃┌┐└┘├┤┬┴┼╭╮╰╯╞╡╪╫╬═║╒╓╔╕╖╗╘╙╚╛╜╝▀▄▁▂▃▅▆▇█▉▊▋▌▍▎▏▐▔▕▖▗▘▙▚▛▜▝▞▟";

    public Task<string> ParseAsync(IReadOnlyList<SessionLogChunkRecord> chunks, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var lines = ReplayRenderedLines(chunks, cancellationToken);
        var transcript = TryBuildStructuredTranscript(lines);
        if (!string.IsNullOrEmpty(transcript))
            return Task.FromResult(transcript);

        return Task.FromResult(BuildFilteredText(lines));
    }

    private static List<string> ReplayRenderedLines(
        IReadOnlyList<SessionLogChunkRecord> chunks,
        CancellationToken cancellationToken)
    {
        var terminal = new EmulatorTerminal(
            cols: VibeTerminal.DefaultCols,
            rows: VibeTerminal.DefaultRows,
            scrollbackSize: ScrollbackSize);

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (chunk.Content.Length == 0)
                continue;

            terminal.Write(chunk.Content.AsSpan());
        }

        var lines = new List<string>(terminal.Rows + ScrollbackSize);

        foreach (var row in terminal.GetScrollback())
            lines.Add(ConvertRowToText(row));

        var screen = terminal.GetSnapshot();
        var rowBuffer = new TerminalCell[terminal.Cols];
        for (var row = 0; row < terminal.Rows; row++)
        {
            for (var col = 0; col < terminal.Cols; col++)
                rowBuffer[col] = screen[row, col];

            lines.Add(ConvertRowToText(rowBuffer));
        }

        return lines;
    }

    private static string ConvertRowToText(TerminalCell[] row)
    {
        var builder = new StringBuilder(row.Length);
        foreach (var cell in row)
        {
            if (cell.IsWideContinuation)
                continue;

            cell.AppendText(builder, replaceControlWithSpace: true);
        }

        return NormalizeLine(builder.ToString());
    }

    private static string TryBuildStructuredTranscript(IReadOnlyList<string> rawLines)
    {
        var entries = new List<TranscriptEntry>();
        TranscriptEntry? currentAssistant = null;
        var sawPreludeChrome = false;
        var preserveLeadingBlank = false;
        var sawFirstMessage = false;

        foreach (var rawLine in rawLines)
        {
            var line = NormalizeLine(rawLine);
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                if (!sawFirstMessage)
                {
                    if (sawPreludeChrome)
                        preserveLeadingBlank = true;

                    continue;
                }

                currentAssistant?.Lines.Add(string.Empty);
                continue;
            }

            if (ShouldSkipChromeLine(trimmed))
            {
                if (!sawFirstMessage)
                    sawPreludeChrome = true;

                continue;
            }

            if (TryParseUserPrompt(line, out var userText))
            {
                if (userText.Length == 0 || IsUiDecoration(userText))
                    continue;

                TrimTrailingBlankLines(currentAssistant);
                currentAssistant = null;

                entries.Add(new TranscriptEntry("User", userText));
                sawFirstMessage = true;
                continue;
            }

            if (TryParseAssistantPrompt(line, out var assistantText))
            {
                if (assistantText.Length == 0 || ShouldSkipChromeLine(assistantText))
                    continue;

                TrimTrailingBlankLines(currentAssistant);

                currentAssistant = new TranscriptEntry("Agent", assistantText);
                entries.Add(currentAssistant);
                sawFirstMessage = true;
                continue;
            }

            if (!sawFirstMessage || currentAssistant is null || ShouldSkipChromeLine(trimmed))
                continue;

            currentAssistant.Lines.Add(line);
        }

        TrimTrailingBlankLines(currentAssistant);

        if (entries.Count == 0)
            return string.Empty;

        var builder = new StringBuilder();
        if (preserveLeadingBlank)
            builder.AppendLine();

        for (var index = 0; index < entries.Count; index++)
        {
            if (index > 0)
                builder.AppendLine().AppendLine();

            var entry = entries[index];
            builder.Append(entry.Role).Append(": ").AppendLine(entry.Lines[0]);

            for (var lineIndex = 1; lineIndex < entry.Lines.Count; lineIndex++)
                builder.AppendLine(entry.Lines[lineIndex]);
        }

        return builder.ToString().TrimEnd();
    }

    private static bool TryParseUserPrompt(string line, out string content)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("> ", StringComparison.Ordinal)
            || trimmed.StartsWith(">\u00A0", StringComparison.Ordinal)
            || string.Equals(trimmed, ">", StringComparison.Ordinal)
            || trimmed.StartsWith("› ", StringComparison.Ordinal))
        {
            content = trimmed[1..].Trim();
            return true;
        }

        content = string.Empty;
        return false;
    }

    private static bool TryParseAssistantPrompt(string line, out string content)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("● ", StringComparison.Ordinal)
            || trimmed.StartsWith("• ", StringComparison.Ordinal))
        {
            content = trimmed[1..].Trim();
            return true;
        }

        content = string.Empty;
        return false;
    }

    private static void TrimTrailingBlankLines(TranscriptEntry? entry)
    {
        if (entry is null)
            return;

        while (entry.Lines.Count > 1 && entry.Lines[^1].Length == 0)
            entry.Lines.RemoveAt(entry.Lines.Count - 1);
    }

    private static string BuildFilteredText(IEnumerable<string> rawLines)
    {
        var output = new List<string>();
        var recentCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var recentQueue = new Queue<string>();
        var pendingBlankLine = false;

        foreach (var rawLine in rawLines)
        {
            var line = NormalizeLine(rawLine);
            if (string.IsNullOrWhiteSpace(line))
            {
                pendingBlankLine = output.Count > 0;
                continue;
            }

            var trimmed = line.Trim();
            if (ShouldSkipFilteredLine(trimmed))
                continue;

            var stripped = StripLeadMarker(trimmed);
            if (stripped.Length == 0 || ShouldSkipFilteredLine(stripped))
                continue;

            if (recentCounts.ContainsKey(stripped))
                continue;

            if (pendingBlankLine && output.Count > 0 && output[^1].Length > 0)
                output.Add(string.Empty);

            pendingBlankLine = false;
            output.Add(stripped);
            RememberRecentLine(stripped, recentQueue, recentCounts);
        }

        while (output.Count > 0 && output[^1].Length == 0)
            output.RemoveAt(output.Count - 1);

        return string.Join("\n", output);
    }

    private static string NormalizeLine(string rawLine)
    {
        if (string.IsNullOrEmpty(rawLine))
            return string.Empty;

        return rawLine
            .Replace('\u00A0', ' ')
            .Replace("\u200B", string.Empty, StringComparison.Ordinal)
            .Replace("\u200C", string.Empty, StringComparison.Ordinal)
            .Replace("\u200D", string.Empty, StringComparison.Ordinal)
            .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
            .TrimEnd();
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

    private static bool ShouldSkipFilteredLine(string line)
    {
        return IsUiDecoration(line)
            || IsShellNoise(line)
            || IsUserPromptLine(line)
            || IsCliChrome(line)
            || IsToolNoise(line)
            || IsProgressNoise(line);
    }

    private static bool ShouldSkipChromeLine(string line)
    {
        return IsUiDecoration(line)
            || IsShellNoise(line)
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

    private static bool IsUserPromptLine(string line)
    {
        return line.StartsWith("› ", StringComparison.Ordinal)
            || line.StartsWith("> ", StringComparison.Ordinal)
            || string.Equals(line, ">", StringComparison.Ordinal);
    }

    private static bool IsCliChrome(string line)
    {
        var trimmed = line.TrimStart();

        if (trimmed.StartsWith("▐", StringComparison.Ordinal)
            || trimmed.StartsWith("▝", StringComparison.Ordinal)
            || trimmed.StartsWith("▘▘", StringComparison.Ordinal))
        {
            return true;
        }

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

        if (line.Contains("high effort", StringComparison.OrdinalIgnoreCase)
            && line.Contains("Claude Max", StringComparison.OrdinalIgnoreCase))
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
            " /mcp",
            "? for shortcuts");
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

        if (line[0] is '✻' or '✽' or '✶' or '◐' or '◑' or '◒' or '◓')
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

    private sealed class TranscriptEntry
    {
        public TranscriptEntry(string role, string firstLine)
        {
            Role = role;
            Lines = [firstLine];
        }

        public string Role { get; }

        public List<string> Lines { get; }
    }
}
