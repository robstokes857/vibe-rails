using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

public class InputAccumulatorTests
{
    private const char ESC = '\x1B';

    /// <summary>
    /// Collects all lines that complete during a test run. Replaces the channel-based
    /// async callback with a synchronous list append so we don't have to pump tasks.
    /// </summary>
    private sealed class CaptureCallback
    {
        public readonly List<string> Lines = new();

        public Func<string, Task> AsCallback => line =>
        {
            lock (Lines) { Lines.Add(line); }
            return Task.CompletedTask;
        };
    }

    private static async Task<List<string>> DrainAsync(InputAccumulator acc, CaptureCallback capture, int expected)
    {
        // The accumulator writes to an internal channel and a worker task pumps the callback.
        // Poll the capture list until it stabilizes (or times out).
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            lock (capture.Lines)
            {
                if (capture.Lines.Count >= expected) break;
            }
            await Task.Delay(10);
        }
        lock (capture.Lines) { return capture.Lines.ToList(); }
    }

    [Fact]
    public async Task Append_PlainNewline_StillTerminatesLine()
    {
        var capture = new CaptureCallback();
        await using var acc = new InputAccumulator(capture.AsCallback);

        acc.Append("hello\n");

        var lines = await DrainAsync(acc, capture, expected: 1);
        Assert.Single(lines);
        Assert.Equal("hello", lines[0]);
    }

    [Fact]
    public async Task Append_BracketedPaste_BuffersEmbeddedNewlines_AsSingleBlock()
    {
        var capture = new CaptureCallback();
        await using var acc = new InputAccumulator(capture.AsCallback);

        // ESC[200~ line1 \n line2 ESC[201~
        acc.Append($"{ESC}[200~line1\nline2{ESC}[201~");

        var lines = await DrainAsync(acc, capture, expected: 1);
        Assert.Single(lines);
        Assert.Equal("line1\nline2", lines[0]);
    }

    [Fact]
    public async Task Append_BracketedPaste_WithTrailingNewline_StillOneBlock()
    {
        var capture = new CaptureCallback();
        await using var acc = new InputAccumulator(capture.AsCallback);

        acc.Append($"{ESC}[200~line1\nline2\n{ESC}[201~");

        var lines = await DrainAsync(acc, capture, expected: 1);
        Assert.Single(lines);
        Assert.Equal("line1\nline2\n", lines[0]);
    }

    [Fact]
    public async Task Append_BracketedPaste_FollowedByRegularLine()
    {
        var capture = new CaptureCallback();
        await using var acc = new InputAccumulator(capture.AsCallback);

        // Paste first, then a separate regular line entry
        acc.Append($"{ESC}[200~line1\nline2{ESC}[201~");
        acc.Append("regular\n");

        var lines = await DrainAsync(acc, capture, expected: 2);
        Assert.Equal(2, lines.Count);
        Assert.Equal("line1\nline2", lines[0]);
        Assert.Equal("regular", lines[1]);
    }

    [Fact]
    public async Task Append_ThreeLinePaste_ProducesSingleRow()
    {
        var capture = new CaptureCallback();
        await using var acc = new InputAccumulator(capture.AsCallback);

        acc.Append($"{ESC}[200~line1\nline2\nline3{ESC}[201~");

        var lines = await DrainAsync(acc, capture, expected: 1);
        Assert.Single(lines);
        Assert.Equal("line1\nline2\nline3", lines[0]);
    }

    [Fact]
    public async Task Append_BracketedPaste_IgnoresCrWithinBlock()
    {
        // Some terminals send \r\n — the accumulator should treat both as embedded newlines
        // and not split. Current behavior: \r becomes \n (normalized) inside the paste.
        var capture = new CaptureCallback();
        await using var acc = new InputAccumulator(capture.AsCallback);

        acc.Append($"{ESC}[200~line1\r\nline2{ESC}[201~");

        var lines = await DrainAsync(acc, capture, expected: 1);
        Assert.Single(lines);
        // Both \r and \n normalize to \n when inside bracketed paste, so we get two \n's.
        // Accept either "line1\nline2" (if \r is filtered by non-bracketed-paste handling)
        // or "line1\n\nline2" — the key is that no split UserInputs rows occur.
        Assert.Contains("line1", lines[0]);
        Assert.Contains("line2", lines[0]);
    }

    [Fact]
    public async Task Append_NonBracketedCsi_IsStillFilteredOut()
    {
        var capture = new CaptureCallback();
        await using var acc = new InputAccumulator(capture.AsCallback);

        // Cursor-left (CSI D) followed by text. The CSI D should be silently consumed.
        acc.Append($"{ESC}[Dhello\n");

        var lines = await DrainAsync(acc, capture, expected: 1);
        Assert.Single(lines);
        Assert.Equal("hello", lines[0]);
    }
}
