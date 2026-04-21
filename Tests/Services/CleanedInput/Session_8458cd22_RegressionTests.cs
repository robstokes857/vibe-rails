using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.CleanedInput;
using VibeRails.Services.UserInOut;
using Xunit;

namespace Tests.Services.CleanedInput;

/// <summary>
/// Regression for session 8458cd22-0641-45c9-afd3-a936032bf3db — a Claude Code
/// chat where the cleaned text was consistently worse than the raw input:
/// <list type="bullet">
///   <item><c>TryContainingLine</c> picked the longest TUI line containing the
///     needle, so pasted prompts came back fused with decorative wrap rows
///     (box-drawing, status widgets, character-by-character echoes).</item>
///   <item><c>TryPromptLine</c>'s fuzzy matcher latched onto previous prompts
///     whose tail happened to match the current needle's tail, overwriting the
///     current row's text with text from an earlier message.</item>
/// </list>
/// The fix: anchor on <c>&gt;</c> / <c>›</c>, require BOTH head and tail to match
/// the raw (unless raw begins with whitespace — the autocomplete-truncated case),
/// and apply length bounds so box-drawing rows can never win.
/// </summary>
public class Session_8458cd22_RegressionTests
{
    private static CleanedUserInputService CreateService()
    {
        var repo = new Mock<IRepository>();
        repo.Setup(r => r.GetSessionLogChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionLogChunkRecord>());
        return new(repo.Object, new TuiTextExtractor(), NullLogger<CleanedUserInputService>.Instance);
    }

    [Fact]
    public void CleanText_MultilinePastedText_ReturnsRawUnchanged()
    {
        // Seq 3: user pasted a multi-line UI dump. TUI has a widget showing
        // "Docs0" on its own line, so the old extractor returned just "Docs0".
        var raw = "Vibe Rails AI\n\nQuery & Inspect Captures\n\nDocs0\n\nSessions0\n\nQueries0";
        var tui = "status bar\nDocs0\nother stuff\nmore stuff";

        var cleaned = CreateService().CleanText(raw, tui);

        // Multi-line raws must never be reduced to a single line from the TUI.
        Assert.NotNull(cleaned);
        Assert.Contains("Vibe Rails AI", cleaned);
        Assert.Contains("Query & Inspect Captures", cleaned);
        Assert.NotEqual("Docs0", cleaned);
    }

    [Fact]
    public void CleanText_FusedPromptLine_ReturnsJustThePromptContent()
    {
        // Seq 10: Claude Code renders the live input widget with column
        // wrapping + status decoration fused into a single sanitized line,
        // with the actual submission after the final `> `. The old extractor
        // returned the entire 500-char decorated line.
        var raw = "Ok so when the page loads I want to show recent captures by default load them automatically. Also, I want them loaded by session. So when you click on one I want to see all the captures for that session.";
        var tui = "wan ─────────────────────────────────────────────────────────⏵⏵automodeon (shift+tabtocycle)newtask?/clear tosave126ktokenst t o s e e a l l t h e c a p t u r e s f o er r t h a t s e s s i o n . > Ok so when the page loads I want to show recent captures by default load them automatically. Also, I want them loaded by session. So when you click on one I want to see all the captures for that session.\n";

        var cleaned = CreateService().CleanText(raw, tui);

        Assert.NotNull(cleaned);
        Assert.DoesNotContain("─", cleaned);
        Assert.DoesNotContain("⏵", cleaned);
        Assert.DoesNotContain("t t o s e e", cleaned);
        Assert.StartsWith("Ok so when the page loads", cleaned);
        Assert.EndsWith("for that session.", cleaned);
    }

    [Fact]
    public void CleanText_PreviousPromptWithSameTail_DoesNotOverwriteCurrentInput()
    {
        // Seq 17: two consecutive paste chunks; the TUI's final prompt line
        // ended with the current needle's tail but started with the PREVIOUS
        // message's head. The old extractor returned "There is a get session
        // button... probably the issue." — replacing row N's text with row N-1 + N.
        var raw = "neither work well... I think they have like a set col X row lock please fix this up. it isn't painited right so that is probably the issue.";
        var tui = "> There is a get session button. It opens a modal and replays the xterm.js session. There is another one in the chat-history-sidebar.js neither work well... I think they have like a set col X row lock please fix this up. it isn't painited right so that is probably the issue.\n";

        var cleaned = CreateService().CleanText(raw, tui);

        // Head of raw ("neither") ≠ head after `> ` ("There is") → reject, return raw.
        Assert.NotNull(cleaned);
        Assert.DoesNotContain("There is a get session button", cleaned);
        Assert.StartsWith("neither work well", cleaned);
    }

    [Fact]
    public void CleanText_BoxDrawingEchoOnly_DoesNotWin()
    {
        // Seq 25: raw was just "ession ID" (10 chars captured mid-typing).
        // The old extractor picked up a 749-char decorative line containing
        // the substring. No candidate should ever be allowed to be that much
        // longer than the needle.
        var raw = "ession ID";
        var tui = "────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────── b db3df3bf2b3203603693a9-a3-d3fdaf-a9-c95c45-41-416406-02-22d2cd8c584584 Session ID 8458cd22-0641-45c9-afd3-a936032bf3db\n";

        var cleaned = CreateService().CleanText(raw, tui);

        Assert.DoesNotContain("─", cleaned ?? string.Empty);
    }

    [Fact]
    public void CleanText_LeadingWhitespaceRaw_ExtractsFullPromptAfterAutocomplete()
    {
        // Row2 from session 11553f24 — kept here so the Session_8458cd22 fix
        // doesn't regress it: raw starts with a keystroke artifact (\t), so
        // tail-only match is acceptable because the user's raw capture is a
        // known-partial keystroke tail from autocomplete, not a complete thought.
        var raw = "\t I only want to run this for codex. If it is not codex just return.";
        var tui = "> @VibeRails/Services/Terminal/Observers/WaitingForUserInputObserver.cs I only want to run this for codex. If it is not codex just return.\n";

        var cleaned = CreateService().CleanText(raw, tui);

        Assert.Equal(
            "@VibeRails/Services/Terminal/Observers/WaitingForUserInputObserver.cs I only want to run this for codex. If it is not codex just return.",
            cleaned);
    }

    [Fact]
    public void CleanText_HeadAndTailMatchWithinBounds_Extracts()
    {
        // When the TUI echo is the same submission, slightly longer (trailing
        // whitespace or punctuation), extraction is still correct.
        var raw = "find me please";
        var tui = "> find me please\n";

        var cleaned = CreateService().CleanText(raw, tui);

        Assert.Equal("find me please", cleaned);
    }
}
