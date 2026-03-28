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
        var (lines, _) = ReplayRenderedLines(chunks, cancellationToken);
        return Task.FromResult(BuildCleanText(lines));
    }

    public Task<string> ParseTranscriptAsync(
        IReadOnlyList<SessionLogChunkRecord> chunks,
        IReadOnlyList<UserInputRecord> userInputs,
        CancellationToken cancellationToken = default)
    {
        if (userInputs.Count == 0)
            throw new InvalidOperationException("This session has no user messages.");


        // Take snapshots of the screen at each user-input boundary.
        // The screen is always correct (scrollback can contain garbled TUI animation states).
        var orderedInputs = userInputs.OrderBy(u => u.Sequence).ToList();
        var snapshots = ReplayWithSnapshots(chunks, orderedInputs, cancellationToken);

        return Task.FromResult(BuildTranscriptFromSnapshots(orderedInputs, snapshots));
    }

    private static (List<string> Lines, int ScreenStartIndex) ReplayRenderedLines(
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

        var screenStart = lines.Count;

        var screen = terminal.GetSnapshot();
        var rowBuf = new TerminalCell[terminal.Cols];
        for (var r = 0; r < terminal.Rows; r++)
        {
            for (var c = 0; c < terminal.Cols; c++)
                rowBuf[c] = screen[r, c];
            lines.Add(RowToText(rowBuf));
        }

        return (lines, screenStart);
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
    /// Replays chunks through the emulator, snapshotting the screen state at each user-input
    /// timestamp boundary. Returns one snapshot per user input (the state AFTER the agent
    /// finished responding) plus a final snapshot after all bytes.
    /// Key insight: the screen is always correct; scrollback may have garbled TUI animations.
    /// </summary>
    private static List<string[]> ReplayWithSnapshots(
        IReadOnlyList<SessionLogChunkRecord> chunks,
        IReadOnlyList<UserInputRecord> orderedInputs,
        CancellationToken cancellationToken)
    {
        var terminal = new EmulatorTerminal(
            cols: VibeTerminal.DefaultCols,
            rows: VibeTerminal.DefaultRows,
            scrollbackSize: ScrollbackSize);

        var snapshots = new List<string[]>();
        var inputIndex = 0;

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Before feeding this chunk, check if we've crossed a user input timestamp.
            // The snapshot captures the screen BEFORE the user's next input — i.e., the
            // agent's response to the previous input is complete.
            while (inputIndex < orderedInputs.Count
                   && chunk.TimestampUtc >= orderedInputs[inputIndex].TimestampUTC)
            {
                snapshots.Add(terminal.GetScreenText());
                inputIndex++;
            }

            if (chunk.Content.Length > 0)
                terminal.Write(chunk.Content.AsSpan());
        }

        // Remaining inputs that had no later chunks
        while (inputIndex < orderedInputs.Count)
        {
            snapshots.Add(terminal.GetScreenText());
            inputIndex++;
        }

        // Final snapshot: screen state after all bytes (agent response to last input)
        snapshots.Add(terminal.GetScreenText());

        return snapshots;
    }

    private static string BuildTranscriptFromSnapshots(
        IReadOnlyList<UserInputRecord> orderedInputs,
        IReadOnlyList<string[]> snapshots)
    {
        // snapshots[0..N-1] = screen before each user input
        // snapshots[N] = final screen after all bytes
        // Agent response to input i = new content between snapshots[i] and snapshots[i+1]

        var turns = new List<(List<string> UserTexts, List<string> AgentLines)>();

        for (var i = 0; i < orderedInputs.Count; i++)
        {
            var afterSnapshot = snapshots[i + 1]; // screen after agent responded
            var agentLines = ExtractAgentFromSnapshot(afterSnapshot);

            if (agentLines.Count == 0 && turns.Count > 0)
            {
                // No agent response — merge into previous turn (multi-line paste)
                turns[^1].UserTexts.Add(orderedInputs[i].InputText.Trim());
            }
            else
            {
                turns.Add(([orderedInputs[i].InputText.Trim()], agentLines));
            }
        }

        return FormatTurns(turns);
    }

    /// <summary>
    /// Extracts agent response text from a screen snapshot by finding the LAST agent
    /// marker (● • ✦) on the screen and taking everything from that point down.
    /// </summary>
    private static List<string> ExtractAgentFromSnapshot(string[] screenRows)
    {
        // Find the last agent bullet marker on screen — that's the most recent response
        var lastAgentRow = -1;
        for (var i = screenRows.Length - 1; i >= 0; i--)
        {
            var trimmed = screenRows[i].Trim();
            if (TryStripAgentMarker(trimmed, out _))
            {
                lastAgentRow = i;
                break;
            }
        }

        if (lastAgentRow < 0)
            return [];

        var lines = new List<string>();
        for (var i = lastAgentRow; i < screenRows.Length; i++)
            lines.Add(screenRows[i]);

        return CleanAgentLines(lines);
    }

    private static string FormatTurns(List<(List<string> UserTexts, List<string> AgentLines)> turns)
    {
        if (turns.Count == 0)
            return "(empty chat)";

        var sb = new StringBuilder();

        foreach (var (userTexts, agentLines) in turns)
        {
            if (sb.Length > 0)
                sb.AppendLine();

            sb.Append("User: ").AppendLine(string.Join("\n", userTexts));

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

    private static List<(int LineIndex, UserInputRecord Input)> FindAnchorsInRange(
        IReadOnlyList<string> lines,
        IReadOnlyList<UserInputRecord> orderedInputs,
        int rangeStart,
        int rangeEnd)
    {
        var anchors = new List<(int, UserInputRecord)>();
        var searchFrom = rangeStart;

        foreach (var input in orderedInputs)
        {
            var text = input.InputText.Trim();
            if (string.IsNullOrEmpty(text))
                continue;

            for (var i = searchFrom; i < rangeEnd; i++)
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

    /// <summary>
    /// Groups anchors into turns. Consecutive user inputs with no agent content
    /// between them are merged into a single user turn (handles multi-line pastes).
    /// </summary>
    private static List<(List<string> UserTexts, List<string> AgentLines)> GroupIntoTurns(
        IReadOnlyList<string> lines,
        IReadOnlyList<(int LineIndex, UserInputRecord Input)> anchors)
    {
        var turns = new List<(List<string> UserTexts, List<string> AgentLines)>();

        for (var a = 0; a < anchors.Count; a++)
        {
            var (userLineIdx, input) = anchors[a];
            var nextUserLineIdx = a + 1 < anchors.Count ? anchors[a + 1].LineIndex : lines.Count;

            var rawAgentLines = new List<string>();
            for (var i = userLineIdx + 1; i < nextUserLineIdx; i++)
                rawAgentLines.Add(lines[i]);

            var agentLines = CleanAgentLines(rawAgentLines);

            if (agentLines.Count == 0 && turns.Count > 0)
            {
                // No agent response for this input — merge into previous turn
                // (handles multi-line pastes where each line is a separate DB record)
                turns[^1].UserTexts.Add(input.InputText.Trim());
            }
            else
            {
                turns.Add(([input.InputText.Trim()], agentLines));
            }
        }

        return turns;
    }

    private static string BuildTranscript(
        IReadOnlyList<string> lines,
        IReadOnlyList<(int LineIndex, UserInputRecord Input)> anchors)
    {
        var turns = GroupIntoTurns(lines, anchors);
        var sb = new StringBuilder();

        foreach (var (userTexts, agentLines) in turns)
        {
            if (sb.Length > 0)
                sb.AppendLine();

            sb.Append("User: ").AppendLine(string.Join("\n", userTexts));

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

        // Status bar / footer chrome (Gemini, Claude, Codex, Copilot)
        if (line.Contains("Shift+Tab to accept", StringComparison.OrdinalIgnoreCase)
            || line.Contains("shift+tab to accept", StringComparison.OrdinalIgnoreCase)
            || line.Contains("no sandbox", StringComparison.OrdinalIgnoreCase)
            || line.Contains("GEMINI.md file", StringComparison.OrdinalIgnoreCase)
            || line.Contains("CLAUDE.md file", StringComparison.OrdinalIgnoreCase)
            || line.Contains("accept edits on", StringComparison.OrdinalIgnoreCase))
            return true;

        // Codex turn delimiters and role labels
        if (line.StartsWith(">>>>>>>>> ", StringComparison.Ordinal)
            || line.StartsWith("<<<<<<<<< ", StringComparison.Ordinal)
            || line == "Assistant said:"
            || line == "User said:"
            || line.StartsWith("Working (", StringComparison.Ordinal))
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
