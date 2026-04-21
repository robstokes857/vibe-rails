using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.CleanedInput;
using VibeRails.Services.UserInOut;
using Xunit;

namespace Tests.Services.CleanedInput;

/// <summary>
/// Regression for session 11553f24-500e-47f0-8e04-b301eb4c2842 — user typed
/// <c>@</c> + TAB to autocomplete a file path. Raw got split into two UserInputs
/// rows and row 1's cleaned text came out as dropdown glued-by-CUP garbage:
/// <c>+.gitattributesWaitingForUserInputObserver Tests/...cs VibeRails/...cs</c>.
///
/// Shape differs from session bf428817: needle has no space between <c>@</c> and
/// filter text (<c>@WaitingForUserInputObserver</c>), and the CUP-fused dropdown
/// line has no whitespace between the glyph and the filename, so
/// <see cref="TuiTextExtractor"/>'s containing-line check no longer finds an
/// <c>@</c>-prefixed substring. The extractor must still prefer the <c>&gt; </c>
/// prompt line over the fuzzy-matched dropdown row.
///
/// Fixtures are the exact windowed session log that
/// <see cref="CleanedInputIdleObserver"/> feeds to the cleaner for each row
/// (bounded by the adjacent UserInputs' timestamps).
/// </summary>
public class Session_11553f24_RegressionTests
{
    private const string ExpectedCleanedPrompt =
        "@VibeRails/Services/Terminal/Observers/WaitingForUserInputObserver.cs I only want to run this for codex. If it is not codex just return.";

    private static CleanedUserInputService CreateService()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.GetSessionLogChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionLogChunkRecord>());
        return new(repo.Object, new TuiTextExtractor(), NullLogger<CleanedUserInputService>.Instance);
    }

    [Fact]
    public void CleanText_Row1_AtSignAutocomplete_IgnoresCupFusedDropdown()
    {
        var svc = CreateService();

        // Window1 is captured before the submission was echoed (idle cleaning ran
        // 5s after paste-close, while the user was still typing the rest). No
        // prompt line exists in the TUI yet, so the cleaner must fall back to the
        // raw input rather than returning the CUP-fused dropdown row.
        var cleaned = svc.CleanText("@WaitingForUserInputObserver", LoadWindow1Fixture());

        Assert.Equal("@WaitingForUserInputObserver", cleaned);
        Assert.DoesNotContain(".gitattributes", cleaned);
    }

    [Fact]
    public void CleanText_Row2_TabPrefixedBody_ExtractsPromptContent()
    {
        var svc = CreateService();

        var cleaned = svc.CleanText(
            "\t I only want to run this for codex. If it is not codex just return.",
            LoadWindow2Fixture());

        Assert.Equal(ExpectedCleanedPrompt, cleaned);
    }

    private static string LoadWindow1Fixture() => LoadFixture("session_11553f24_window1_log.bin");
    private static string LoadWindow2Fixture() => LoadFixture("session_11553f24_window2_log.bin");

    private static string LoadFixture(string name)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Services", "CleanedInput", "Fixtures", name);
        return Encoding.UTF8.GetString(File.ReadAllBytes(path));
    }
}
