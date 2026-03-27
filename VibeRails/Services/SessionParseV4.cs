using System.Text;
using TerminalEmulator;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using EmulatorTerminal = TerminalEmulator.Terminal;
using VibeTerminal = VibeRails.Services.Terminal.Terminal;

namespace VibeRails.Services;

/// <summary>
/// CLI-agnostic session parser. Replays all PTY bytes through a headless terminal emulator
/// to get the final rendered state, then uses known user input texts (from the DB) as anchors
/// to split the output into User/Agent turns. Works for any CLI (Claude, Codex, Gemini, Copilot)
/// because it doesn't depend on CLI-specific prompt markers.
/// </summary>
public sealed class SessionParseV4 : ISessionOutputParser
{
    private const int ScrollbackSize = 20_000;

    private static readonly HashSet<char> DecorativeChars = new(
        " -_=~|─━│┃┌┐└┘├┤┬┴┼╭╮╰╯╞╡╪╫╬═║╒╓╔╕╖╗╘╙╚╛╜╝▀▄▁▂▃▅▆▇█▉▊▋▌▍▎▏▐▔▕▖▗▘▙▚▛▜▝▞▟");

    public Task<string> ParseAsync(IReadOnlyList<SessionLogChunkRecord> chunks, CancellationToken cancellationToken = default)
    {
        var lines = ReplayRenderedLines(chunks, cancellationToken);
        return Task.FromResult(BuildCleanText(lines));
    }

    public Task<string> ParseTranscriptAsync(
        IReadOnlyList<SessionLogChunkRecord> chunks,
        IReadOnlyList<UserInputRecord> userInputs,
        CancellationToken cancellationToken = default)
    {
        var lines = ReplayRenderedLines(chunks, cancellationToken);

        if (userInputs.Count == 0)
            return Task.FromResult(BuildCleanText(lines));

        var anchors = FindUserInputAnchors(lines, userInputs);
        if (anchors.Count == 0)
            return Task.FromResult(BuildCleanText(lines));

        return Task.FromResult(BuildTranscript(lines, anchors));
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

        var lines = new List<string>();

        foreach (var row in terminal.GetScrollback())
            lines.Add(RowToText(row));

        var screen = terminal.GetSnapshot();
        var rowBuf = new TerminalCell[terminal.Cols];
        for (var r = 0; r < terminal.Rows; r++)
        {
            for (var c = 0; c < terminal.Cols; c++)
                rowBuf[c] = screen[r, c];
            lines.Add(RowToText(rowBuf));
        }

        return lines;
    }

    private static string RowToText(TerminalCell[] row)
    {
        var sb = new StringBuilder(row.Length);
        foreach (var cell in row)
        {
            if (cell.IsWideContinuation) continue;
            cell.AppendText(sb, replaceControlWithSpace: true);
        }
        return NormalizeLine(sb.ToString());
    }

    private static string NormalizeLine(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        return raw
            .Replace('\u00A0', ' ')
            .Replace("\u200B", string.Empty, StringComparison.Ordinal)
            .Replace("\u200C", string.Empty, StringComparison.Ordinal)
            .Replace("\u200D", string.Empty, StringComparison.Ordinal)
            .Replace("\uFEFF", string.Empty, StringComparison.Ordinal)
            .TrimEnd();
    }

    /// <summary>
    /// Searches rendered lines for each user input text (in sequence order), returning
    /// the line index and input record for each match. Searches forward from the previous
    /// match to avoid false positives from agents quoting the user.
    /// </summary>
    private static List<(int LineIndex, UserInputRecord Input)> FindUserInputAnchors(
        IReadOnlyList<string> lines,
        IReadOnlyList<UserInputRecord> userInputs)
    {
        var anchors = new List<(int, UserInputRecord)>();
        var searchFrom = 0;

        foreach (var input in userInputs.OrderBy(u => u.Sequence))
        {
            var text = input.InputText.Trim();
            if (string.IsNullOrEmpty(text))
                continue;

            for (var i = searchFrom; i < lines.Count; i++)
            {
                if (lines[i].Contains(text, StringComparison.Ordinal))
                {
                    anchors.Add((i, input));
                    searchFrom = i + 1;
                    break;
                }
            }
        }

        return anchors;
    }

    private static string BuildTranscript(
        IReadOnlyList<string> lines,
        IReadOnlyList<(int LineIndex, UserInputRecord Input)> anchors)
    {
        var sb = new StringBuilder();

        for (var a = 0; a < anchors.Count; a++)
        {
            var (userLineIdx, input) = anchors[a];
            var nextUserLineIdx = a + 1 < anchors.Count ? anchors[a + 1].LineIndex : lines.Count;

            // Collect agent response lines between this user input and the next
            var agentLines = new List<string>();
            for (var i = userLineIdx + 1; i < nextUserLineIdx; i++)
                agentLines.Add(lines[i]);

            // Strip chrome/noise from agent lines
            agentLines = CleanAgentLines(agentLines);

            // Append user turn
            if (sb.Length > 0)
                sb.AppendLine();

            sb.Append("User: ").AppendLine(input.InputText.Trim());

            if (agentLines.Count > 0)
            {
                sb.AppendLine();
                sb.Append("Agent: ").AppendLine(agentLines[0]);
                for (var i = 1; i < agentLines.Count; i++)
                    sb.AppendLine(agentLines[i]);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private static List<string> CleanAgentLines(List<string> lines)
    {
        var cleaned = new List<string>();

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (IsDecorative(trimmed))
                continue;

            if (IsChrome(trimmed))
                continue;

            if (IsProgressNoise(trimmed))
                continue;

            if (IsEchoedUserPrompt(trimmed))
                continue;

            // Always strip agent bullet markers (● • ✦)
            if (TryStripAgentMarker(trimmed, out var stripped))
            {
                cleaned.Add(stripped);
                continue;
            }

            cleaned.Add(trimmed.Length == 0 ? string.Empty : line.TrimEnd());
        }

        // Trim trailing blank lines
        while (cleaned.Count > 0 && string.IsNullOrWhiteSpace(cleaned[^1]))
            cleaned.RemoveAt(cleaned.Count - 1);

        // Trim leading blank lines
        while (cleaned.Count > 0 && string.IsNullOrWhiteSpace(cleaned[0]))
            cleaned.RemoveAt(0);

        return cleaned;
    }

    private static bool TryStripAgentMarker(string line, out string content)
    {
        // Common agent markers across CLIs: ● • ✦
        if (line.Length >= 2 && line[0] is '●' or '•' or '✦' && char.IsWhiteSpace(line[1]))
        {
            content = line[1..].TrimStart();
            return true;
        }
        content = string.Empty;
        return false;
    }

    private static bool IsDecorative(string line)
    {
        if (line.Length == 0)
            return false;

        foreach (var ch in line)
        {
            if (!DecorativeChars.Contains(ch))
                return false;
        }
        return true;
    }

    /// <summary>
    /// Detects lines that are echoed user prompts in the terminal (e.g. "> hi", "› hello").
    /// These are the CLI's rendering of what the user typed — we already have the input from the DB.
    /// </summary>
    private static bool IsEchoedUserPrompt(string line)
    {
        if (line.StartsWith("> ", StringComparison.Ordinal)
            || line.StartsWith("› ", StringComparison.Ordinal)
            || line.StartsWith(">\u00A0", StringComparison.Ordinal)
            || string.Equals(line, ">", StringComparison.Ordinal)
            || string.Equals(line, "›", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    private static bool IsChrome(string line)
    {
        if (line.StartsWith("? for shortcuts", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("esc to interrupt", StringComparison.OrdinalIgnoreCase))
            return true;

        if (line.StartsWith("Tip: ", StringComparison.OrdinalIgnoreCase))
            return true;

        // Box-border chrome: lines enclosed in │ ... │ or similar box-drawing borders
        if (line.Length >= 3 && line[0] is '│' or '┃' or '║' && line[^1] is '│' or '┃' or '║')
            return true;

        // CLI version/header lines
        if (line.Contains("Claude Code v", StringComparison.OrdinalIgnoreCase)
            || line.Contains("OpenAI Codex", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Gemini CLI", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Gemini Code Assist", StringComparison.OrdinalIgnoreCase)
            || line.Contains("Copilot CLI", StringComparison.OrdinalIgnoreCase))
            return true;

        // Model/status info lines
        if (line.Contains("% left", StringComparison.OrdinalIgnoreCase)
            || line.Contains("weekly limit", StringComparison.OrdinalIgnoreCase)
            || line.Contains("/skills", StringComparison.OrdinalIgnoreCase)
            || line.Contains("/model", StringComparison.OrdinalIgnoreCase)
            || line.Contains("MCP startup", StringComparison.OrdinalIgnoreCase)
            || line.Contains("MCP issues", StringComparison.OrdinalIgnoreCase))
            return true;

        // Directory info lines (often in CLI headers)
        if (line.StartsWith("directory:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("model:", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static bool IsProgressNoise(string line)
    {
        if (line.Length == 0)
            return false;

        if (line[0] is '✻' or '✽' or '✶' or '◐' or '◑' or '◒' or '◓')
            return true;

        if (line.StartsWith("⎿", StringComparison.Ordinal))
            return true;

        return false;
    }

    /// <summary>
    /// Fallback: returns cleaned text without User/Agent structure.
    /// Strips decorative lines and collapses excessive blank lines.
    /// </summary>
    private static string BuildCleanText(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        var blankCount = 0;

        foreach (var line in lines)
        {
            var trimmed = line.Trim();

            if (trimmed.Length == 0)
            {
                blankCount++;
                if (blankCount <= 2)
                    sb.AppendLine();
                continue;
            }

            if (IsDecorative(trimmed))
                continue;

            blankCount = 0;
            sb.AppendLine(line);
        }

        return sb.ToString().Trim();
    }
}
