using System.Text.Json;
using VibeRails.DTOs;
using Xunit;

namespace Tests;

public sealed class AppJsonSerializerContextTests
{
    [Fact]
    public void IncludesUpdateComputerNameDto_ForMinimalApiBodyBinding()
    {
        var json = JsonSerializer.Serialize(
            new UpdateComputerNameDto("build-box"),
            AppJsonSerializerContext.Default.UpdateComputerNameDto);

        Assert.Equal("""{"computerName":"build-box"}""", json);
    }

    [Fact]
    public void TerminalSnapshotResponse_UsesReservedXtermRendererFieldNames()
    {
        var json = JsonSerializer.Serialize(
            new TerminalSnapshotResponse(
                TabId: "tab-1",
                SessionId: "session-1",
                CapturedUtc: DateTimeOffset.UnixEpoch,
                Cols: 120,
                Rows: 30,
                ScreenText: ["prompt>"],
                XtermUiBytes: new TerminalXtermUiBytes(
                    ContentType: "application/vnd.viberails.xterm-ui-bytes",
                    Encoding: "base64",
                    Format: "ansi-replay",
                    Base64: "cHJvbXB0Pg==",
                    ByteLength: 7,
                    Cols: 120,
                    Rows: 30,
                    IncludesScrollback: true,
                    RendererHint: "xterm.js"),
                XtermPngString: null),
            AppJsonSerializerContext.Default.TerminalSnapshotResponse);

        Assert.Contains("\"xterm_ui_bytes\"", json);
        Assert.Contains("\"xterm_png_string\"", json);
        Assert.Contains("\"byte_length\"", json);
        Assert.Contains("\"includes_scrollback\"", json);
        Assert.DoesNotContain("\"xtermUiBytes\"", json);
    }
}
