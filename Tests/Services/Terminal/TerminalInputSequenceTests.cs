using VibeRails.DTOs;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

public sealed class TerminalInputSequenceTests
{
    [Fact]
    public async Task NotificationUsesSemanticEscapesThenPasteThenEnter()
    {
        var request = new TerminalInputRequest("updated\ncard", Submit: true, EscapeCount: 2, Paste: true);
        var sent = new List<string>();
        await TerminalInputSequence.SendAsync(request, TerminalInputSequence.Prepare(request),
            _ => { sent.Add("physical-escape"); return Task.CompletedTask; },
            (text, _) => { sent.Add(text); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        Assert.Equal(["physical-escape", "physical-escape", "\u001b[200~updated\ncard\u001b[201~", "\r"], sent);
    }

    [Fact]
    public void PasteCannotInjectAnEscapeTerminator()
    {
        var prepared = TerminalInputSequence.Prepare(new("text\u001b[201~\0\r\nmore", Paste: true));
        Assert.Equal("\u001b[200~text[201~\nmore\u001b[201~", prepared);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void InvalidEscapeCountIsRejected(int count) =>
        Assert.Throws<ArgumentException>(() => TerminalInputSequence.Prepare(new("task", EscapeCount: count)));

    [Fact]
    public void OversizedUtf8InputIsRejected() =>
        Assert.Throws<ArgumentException>(() => TerminalInputSequence.Prepare(new(new string('界', TerminalControlProtocol.MaxMessageBytes / 2), Paste: true)));
}
