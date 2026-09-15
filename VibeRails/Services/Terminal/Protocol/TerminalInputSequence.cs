using System.Text;
using VibeRails.DTOs;

namespace VibeRails.Services.Terminal;

/// <summary>Validated tool input; paste payloads cannot inject terminal control sequences.</summary>
internal static class TerminalInputSequence
{
    internal static string Prepare(TerminalInputRequest request)
    {
        if (string.IsNullOrEmpty(request.Text)) throw new ArgumentException("Input text is required.");
        if (request.EscapeCount is < 0 or > 2) throw new ArgumentException("Escape count must be between zero and two.");
        var text = request.Text;
        if (request.Paste)
        {
            text = new string(text.Where(ch => !char.IsControl(ch) || ch is '\n' or '\r' or '\t').ToArray());
            text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            text = "\u001b[200~" + text + "\u001b[201~";
        }
        if (Encoding.UTF8.GetByteCount(text) + (request.Submit ? 1 : 0) > TerminalControlProtocol.MaxMessageBytes)
            throw new ArgumentException($"Input exceeds {TerminalControlProtocol.MaxMessageBytes} bytes.");
        return text;
    }

    internal static async Task SendAsync(TerminalInputRequest request, string prepared,
        Func<CancellationToken, Task> escape, Func<string, CancellationToken, Task> send, CancellationToken ct)
    {
        for (var i = 0; i < request.EscapeCount; i++)
        {
            await escape(ct);
            await Task.Delay(100, ct);
        }
        if (!request.Paste)
        {
            await send(request.Submit ? prepared + "\r" : prepared, ct);
            return;
        }
        await send(prepared, ct);
        if (request.Submit)
        {
            await Task.Delay(100, ct);
            await send("\r", ct);
        }
    }
}
