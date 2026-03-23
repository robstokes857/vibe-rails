using System.Text;
using System.Text.RegularExpressions;
using TerminalEmulator;
using VibeRails.DTOs;
using VibeRails.Interfaces;
using EmulatorTerminal = TerminalEmulator.Terminal;
using VibeTerminal = VibeRails.Services.Terminal.Terminal;

namespace VibeRails.Services;

/// <summary>
/// Alternative session parser that replays raw PTY bytes through a headless terminal emulator
/// and dumps the final visual grid state (scrollback + screen) as plain text — no filtering,
/// no deduplication. Use this to see exactly what the terminal showed.
/// </summary>
public sealed class SessionParseV2 : ISessionOutputParser
{
    private const int ScrollbackSize = 20_000;

    public Task<string> ParseAsync(IReadOnlyList<SessionLogChunkRecord> chunks, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var terminal = new EmulatorTerminal(
            cols: VibeTerminal.DefaultCols,
            rows: VibeTerminal.DefaultRows,
            scrollbackSize: ScrollbackSize);

        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            terminal.Write(chunk.Content.AsSpan());
        }

        var sb = new StringBuilder();

        foreach (var row in terminal.GetScrollback())
        {
            AppendRow(sb, row);
        }

        var screen = terminal.GetSnapshot();
        var rowBuf = new TerminalCell[terminal.Cols];
        for (var r = 0; r < terminal.Rows; r++)
        {
            for (var c = 0; c < terminal.Cols; c++)
                rowBuf[c] = screen[r, c];
            AppendRow(sb, rowBuf);
        }

        return Task.FromResult(TrimOutput(sb.ToString()));
    }

    private static void AppendRow(StringBuilder sb, TerminalCell[] row)
    {
        var rowSb = new StringBuilder();
        foreach (var cell in row)
        {
            if (cell.IsWideContinuation) continue;
            cell.AppendText(rowSb, replaceControlWithSpace: true);
        }
        sb.AppendLine(rowSb.ToString().TrimEnd());
    }

    private static string TrimOutput(string text)
    {
        var collapsed = Regex.Replace(text, @"(\r?\n){3,}", "\n\n");
        return collapsed.Trim();
    }
}
