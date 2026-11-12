using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

/// <summary>
/// The request log line is written before authentication. Text from the request line must never
/// start a second record or grow without bound (VIBE-310).
/// </summary>
public sealed class RequestLogTextTests
{
    [Theory]
    [InlineData("/api/v1/board/cards", "/api/v1/board/cards")]
    [InlineData("/files/My Document (1).txt", "/files/My Document (1).txt")]
    [InlineData("/caf\u00E9/\u65E5x\u672C", "/caf\u00E9/\u65E5x\u672C")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void OrdinaryPathsAreUnchanged(string? path, string expected) => Assert.Equal(expected, RequestLogText.Path(path));

    [Fact]
    public void ADecodedLineBreakCannotStartAForgedRecord()
    {
        var forged = "/x\n2026-11-12 10:00:00.000 [WRN] [Auth] Token rotated\r\n[HTTP <-] GET /";

        var logged = RequestLogText.Path(forged);

        Assert.DoesNotContain('\n', logged);
        Assert.DoesNotContain('\r', logged);
        Assert.Equal("/x\\n2026-11-12 10:00:00.000 [WRN] [Auth] Token rotated\\r\\n[HTTP <-] GET /", logged);
    }

    [Theory]
    [InlineData("/a\u001B[2Jb", "/a\\u001B[2Jb")]          // terminal escape
    [InlineData("/a\u0009b\u0000c", "/a\\u0009b\\u0000c")]  // other controls
    [InlineData("/a\u202Eb", "/a\\u202Eb")]                // right-to-left override
    [InlineData("/a\u2028b\u2029c", "/a\\u2028b\\u2029c")]  // line and paragraph separators
    [InlineData("/a\u200Bb", "/a\\u200Bb")]                // zero-width space
    public void ControlsAndDisplayControlsAreMadeVisible(string path, string expected)
    {
        Assert.Equal(expected, RequestLogText.Path(path));
        Assert.Equal(expected[1..], RequestLogText.Query(path[1..]));
    }

    [Fact]
    public void LongValuesAreBoundedAndSayHowMuchWasCut()
    {
        var path = "/" + new string('a', RequestLogText.MaxLength + 100);

        var logged = RequestLogText.Path(path);

        Assert.StartsWith("/" + new string('a', RequestLogText.MaxLength - 1), logged, StringComparison.Ordinal);
        Assert.EndsWith("(+101)", logged, StringComparison.Ordinal);
        Assert.True(logged.Length < RequestLogText.MaxLength + 16);
        Assert.Equal(new string('q', RequestLogText.MaxLength), RequestLogText.Query(new string('q', RequestLogText.MaxLength)));
    }

    [Fact]
    public void ProgramLogsTheRequestLineThroughTheHelper()
    {
        var program = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "VibeRails", "Program.cs"));

        Assert.Contains("RequestLogText.Path(context.Request.Path.Value)", program, StringComparison.Ordinal);
        Assert.Contains("RequestLogText.Query(RedactAuthTokens(", program, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VibeRails.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
