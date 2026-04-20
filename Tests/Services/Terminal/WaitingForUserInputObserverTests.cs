using System.Text;
using VibeRails.DTOs;
using VibeRails.Services;
using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

public class WaitingForUserInputObserverTests
{
    [Fact]
    public async Task Publishes_WhenSelectionGlyphsArriveAcrossChunks()
    {
        var events = await CaptureEventsAsync(
            "Choose an action:\r\n• Continue\r\n",
            "◦ Cancel\r\n");

        Assert.Single(events);
        Assert.Equal("session_waiting_for_user", events[0].Type);
    }

    [Fact]
    public async Task Publishes_WhenCodexConfirmMenuAppears()
    {
        // Regression: session f7f02667-89c6-483a-a1d8-5a74271077c2 showed Codex at
        // an MCP permission prompt ("› 1. Allow ... enter to submit | esc to cancel").
        // The original observer only knew Claude's •/◦ glyph menus and missed this.
        var events = await CaptureEventsAsync(SplitFixture("session_f7f02667_codex_menu.bin", chunkSize: 256));

        Assert.Contains(events, e => e.Type == "session_waiting_for_user");
    }

    [Fact]
    public async Task DoesNotPublish_WhenCodexIsIdle_AfterSpinnerRedraws()
    {
        // Regression: session ffd56744-3417-430f-8525-68091cf2f023 was an idle Codex
        // at its normal prompt. The observer fired a false positive during the
        // spinner/redraw phase because its 16 KB rolling buffer was not frame-aware
        // and accumulated bullet-glyph lines across multiple redraws.
        var events = await CaptureEventsAsync(SplitFixture("session_ffd56744_codex_idle.bin", chunkSize: 512));

        Assert.DoesNotContain(events, e => e.Type == "session_waiting_for_user");
    }

    [Fact]
    public async Task DoesNotPublish_WhenSpinnerFramesContainWorkingFragmentsNearGlyphs()
    {
        var events = await CaptureEventsAsync(
            "• Wor",
            "king... ◦\r\n");

        Assert.Empty(events);
    }

    [Fact]
    public async Task Publishes_WhenWorkingTextIsNotNearTheSelectionGlyphs()
    {
        var events = await CaptureEventsAsync(
            "Working...\r\n",
            new string('x', 80),
            "\r\nChoose an action:\r\n• Continue\r\n◦ Cancel\r\n");

        Assert.Single(events);
        Assert.Equal("session_waiting_for_user", events[0].Type);
    }

    [Fact]
    public async Task DoesNotPublish_WhenAssistantReplyContainsNestedBulletList()
    {
        var events = await CaptureEventsAsync(
            "Here are the steps:\r\n",
            "• First step\r\n",
            "  ◦ Detail\r\n");

        Assert.Empty(events);
    }

    private static async Task<List<AppEvent>> CaptureEventsAsync(params string[] chunks)
    {
        var eventBus = new AppEventBus();
        var observer = new WaitingForUserInputObserver(eventBus);
        var events = new List<AppEvent>();
        using var subscription = eventBus.Subscribe(events.Add);

        foreach (var chunk in chunks)
        {
            await observer.OnTerminalIoAsync(new TerminalIoEvent(
                SessionId: "session-1",
                Direction: TerminalIoDirection.Output,
                Source: TerminalIoSource.Pty,
                Text: chunk,
                TimestampUtc: DateTimeOffset.UtcNow));
        }

        return events;
    }

    private static string LoadFixture(string fileName)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Services", "Terminal", "Fixtures", fileName);
        return Encoding.UTF8.GetString(File.ReadAllBytes(path));
    }

    /// <summary>
    /// Split a fixture into fixed-size byte slices (decoded to UTF-8 strings) so
    /// the observer sees many small TerminalIoEvents — the same way the PTY
    /// consumer feeds it in production. Splitting at byte boundaries can straddle
    /// multi-byte UTF-8 sequences; to stay safe we decode the whole file once
    /// then slice the string at char boundaries.
    /// </summary>
    private static string[] SplitFixture(string fileName, int chunkSize)
    {
        var text = LoadFixture(fileName);
        var chunks = new List<string>();
        for (var i = 0; i < text.Length; i += chunkSize)
        {
            chunks.Add(text.Substring(i, Math.Min(chunkSize, text.Length - i)));
        }
        return chunks.ToArray();
    }
}
