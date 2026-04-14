using VibeRails.Services.UserInOut;
using Xunit;

namespace Tests.Services.BertV2;

public class ScreenTextMatcherTests
{
    [Fact]
    public void TryMatch_FindsEnhancedVersion_WithFilePrefix()
    {
        var accumulated = "can you add something so I can search by session id?";
        var screenLines = new[]
        {
            "",
            "some other output line",
            "> @VibeRails/wwwroot/js/modules/chat-history-sidebar.js can you add something so I can search by session id?",
            "",
            "· Pondering…",
        };

        var result = ScreenTextMatcher.TryMatch(accumulated, screenLines);

        Assert.NotNull(result);
        Assert.Contains("@VibeRails", result);
        Assert.Contains("can you add something", result);
    }

    [Fact]
    public void TryMatch_ReturnsNull_WhenNoMatch()
    {
        var accumulated = "deploy to production";
        var screenLines = new[]
        {
            "PowerShell 7.5.4",
            "PS C:\\source>",
            "",
        };

        Assert.Null(ScreenTextMatcher.TryMatch(accumulated, screenLines));
    }

    [Fact]
    public void TryMatch_ReturnsNull_WhenNeedleTooShort()
    {
        var accumulated = "hi";
        var screenLines = new[] { "> hi there" };

        Assert.Null(ScreenTextMatcher.TryMatch(accumulated, screenLines));
    }

    [Fact]
    public void TryMatch_ReturnsNull_WhenScreenEmpty()
    {
        var accumulated = "add search functionality";
        Assert.Null(ScreenTextMatcher.TryMatch(accumulated, Array.Empty<string>()));
    }

    [Fact]
    public void TryMatch_PrefersLongerMatch()
    {
        var accumulated = "add search functionality";
        var screenLines = new[]
        {
            "> add search functionality please also add filtering",
            "> add search functionality",
            "",
        };

        var result = ScreenTextMatcher.TryMatch(accumulated, screenLines);

        Assert.NotNull(result);
        // Should prefer the longer line that contains the needle
        Assert.Contains("filtering", result);
    }

    [Fact]
    public void TryMatch_StripsPromptPrefix()
    {
        var accumulated = "refactor the database module";
        var screenLines = new[]
        {
            "",
            "> refactor the database module and add proper error handling",
            "",
        };

        var result = ScreenTextMatcher.TryMatch(accumulated, screenLines);

        Assert.NotNull(result);
        Assert.DoesNotContain("> ", result);
    }

    [Fact]
    public void TryMatch_ReturnsNull_WhenMatchIsNotLonger()
    {
        // If the screen version is the same length, no enhancement
        var accumulated = "refactor the database module";
        var screenLines = new[]
        {
            "> refactor the database module",
        };

        // The screen version after prompt strip is the same — no improvement
        Assert.Null(ScreenTextMatcher.TryMatch(accumulated, screenLines));
    }

    [Fact]
    public void TryMatch_HandlesTokenOverlap()
    {
        // The accumulated text has most of the same words but is shorter
        // and the screen version has additional context
        var accumulated = "search sessions by id in sidebar";
        var screenLines = new[]
        {
            "",
            "> @chat-history-sidebar.js search sessions by id in sidebar with GUID lookup",
            "",
        };

        var result = ScreenTextMatcher.TryMatch(accumulated, screenLines);

        Assert.NotNull(result);
        Assert.Contains("GUID lookup", result);
    }
}
