using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// Pins the invariant that a reserved control frame is never mistaken for user input.
/// Session 71dee36a (GLM 5.2, 2026-07-26) sent `__resize__:171,4` from a VS Code panel
/// dragged to a 4-row sliver; rows=4 fails the rows>=5 bound, the caller treated
/// "didn't parse" as "not a control message", and the literal text was written to PTY
/// stdin — appearing typed into OpenCode's composer. See runbooks/terminal/TERMINAL.md
/// "## 2026-07-26 __resize__:171,4 typed into the OpenCode composer".
/// </summary>
public sealed class TerminalControlProtocolTests
{
    [Theory]
    [InlineData("__resize__:120,40", 120, 40)]
    [InlineData("__resize__: 120 , 40 ", 120, 40)]
    [InlineData("__resize__:10,5", 10, 5)]
    [InlineData("__resize__:1000,500", 1000, 500)]
    public void TryParseResizeCommand_AcceptsInRangeGeometry(string frame, int expectedCols, int expectedRows)
    {
        Assert.True(TerminalControlProtocol.TryParseResizeCommand(frame, out var cols, out var rows));
        Assert.Equal(expectedCols, cols);
        Assert.Equal(expectedRows, rows);
    }

    [Theory]
    [InlineData("__resize__:171,4")]   // the real frame from session 71dee36a
    [InlineData("__resize__:171,0")]
    [InlineData("__resize__:9,40")]
    [InlineData("__resize__:1001,40")]
    [InlineData("__resize__:120,501")]
    [InlineData("__resize__:120")]
    [InlineData("__resize__:abc,40")]
    [InlineData("__resize__:")]
    public void TryParseResizeCommand_RejectsOutOfRangeOrMalformed(string frame)
    {
        Assert.False(TerminalControlProtocol.TryParseResizeCommand(frame, out _, out _));
    }

    [Theory]
    [InlineData("__resize__:171,4")]
    [InlineData("__resize__:garbage")]
    [InlineData("__resize__:")]
    [InlineData("__cmd__:")]
    [InlineData("__cmd__:not a valid name")]
    [InlineData("__cmd__:replay")]
    public void IsReservedControlFrame_ClaimsEveryReservedPrefix_EvenWhenThePayloadIsInvalid(string frame)
    {
        // This is the guard that makes the class of bug impossible: whatever the
        // payload looks like, a reserved prefix means "control frame", so the caller
        // drops it instead of falling through to the PTY.
        Assert.True(TerminalControlProtocol.IsReservedControlFrame(frame));
    }

    [Theory]
    [InlineData("ls -la")]
    [InlineData("")]
    [InlineData("__resize__")]          // no colon — not the reserved payload form
    [InlineData("echo __resize__:171,4")]
    [InlineData("\x1b[A")]
    public void IsReservedControlFrame_LeavesRealInputAlone(string input)
    {
        Assert.False(TerminalControlProtocol.IsReservedControlFrame(input));
    }

    [Fact]
    public void RejectedResizeFrame_IsStillRecognizedAsReserved()
    {
        // The exact pairing that was broken: parse fails AND the frame is reserved,
        // so the input loop must drop it rather than route it as keystrokes.
        const string frame = "__resize__:171,4";

        Assert.False(TerminalControlProtocol.TryParseResizeCommand(frame, out _, out _));
        Assert.True(TerminalControlProtocol.IsReservedControlFrame(frame));
    }

    [Theory]
    [InlineData("__PIN__:1234", true)]
    [InlineData("__PIN__:123456", true)]
    [InlineData("__PIN__:", true)]
    [InlineData("__PIN_", false)]
    [InlineData("1234", false)]
    [InlineData("", false)]
    public void IsPinResponseFrame_MatchesTheAsciiPrefixOnRawBytes(string frame, bool expected)
    {
        // TerminalRunner uses this on the authorized input hot path to drop stray PIN
        // frames without decoding every keystroke.
        Assert.Equal(
            expected,
            TerminalControlProtocol.IsPinResponseFrame(System.Text.Encoding.UTF8.GetBytes(frame)));
    }

    [Fact]
    public void SanitizeFrameForLog_RedactsPinPayloads()
    {
        // A 4-6 digit PIN in a log file is the whole secret. Whatever else the
        // sanitizer does, the digits must never survive into the log message.
        var sanitized = TerminalControlProtocol.SanitizeFrameForLog("__PIN__:123456");

        Assert.DoesNotContain("123456", sanitized, StringComparison.Ordinal);
        Assert.StartsWith("__PIN__:", sanitized, StringComparison.Ordinal);
        Assert.Contains("redacted", sanitized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SanitizeFrameForLog_StripsControlCharactersAndTruncates()
    {
        var sanitized = TerminalControlProtocol.SanitizeFrameForLog(
            "\x1b[31m__resize__:" + new string('9', 200));

        Assert.DoesNotContain('\x1b', sanitized);
        Assert.True(sanitized.Length <= 64);
        Assert.Contains("__resize__:", sanitized, StringComparison.Ordinal);
    }
}
