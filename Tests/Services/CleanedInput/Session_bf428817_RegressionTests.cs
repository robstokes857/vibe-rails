using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.CleanedInput;
using VibeRails.Services.UserInOut;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.CleanedInput;

/// <summary>
/// Regression tests for session bf428817-9b30-4ca2-b0fc-a993e3a81ab5 — the user
/// pressed TAB autocomplete while typing an <c>@</c>-file reference and the raw
/// UserInputs got split into 2 rows, with garbage cleaned output.
///
/// Three bugs captured:
///   1. Raw capture split at bracketed-paste close (Claude Code uses bracketed
///      paste to insert autocomplete completions). InputAccumulator flushed the
///      pre-paste buffer as a premature line.
///   2. First row's CleanedText pointed at the autocomplete dropdown instead of
///      the submitted prompt. TuiTextExtractor picked a junk line because the
///      finalized prompt line has no <c>\n</c> separating it from the keystroke
///      echo region.
///   3. Second row's CleanedText had keystroke-echo noise (<c>w h y d i e ...</c>)
///      prepended to the correct prompt, joined by <c>&gt; </c>. Same extractor
///      bug — the match ran past the prompt boundary.
/// </summary>
public class Session_bf428817_RegressionTests
{
    private const string ExpectedCleanedPrompt =
        "@ VibeRails/Services/Terminal/Observers/WaitingForUserInputObserver.cs why doesn't this code work unless I am debugging lol? Am I not awaiting something?";

    // Synthetic TUI output that reproduces the pattern observed in the real session log.
    // Dropdown entries land on their own \n-delimited lines; the finalized prompt ends up
    // on the SAME sanitized "line" as the keystroke-echo area because cursor positioning
    // (CUP) doesn't emit \n in the sanitizer.
    private const string DropdownAndPromptTui =
        "+ .claude\\\n" +
        "+ .dockerignore\n" +
        "+ .dotnet\\\n" +
        "+ .dotnet-home\\\n" +
        "+ .git\\\n" +
        // Cursor positioning merges: narrow dropdown entry (`+ .gitattributes`), the user's
        // filter text (`WaitingForUserInputObserver`), and the selected path — all on one
        // sanitized line because the TUI uses CUP sequences between them.
        "+ .gitattributes WaitingForUserInputObserver VibeRails/Services/Terminal/Observers/WaitingForUserInputObserver.cs\n" +
        // The keystroke-by-keystroke echo region + finalized prompt, glued together
        // because CUP moves don't emit \n.
        "w h y  d i e o e s n ' t  t h i s  c o d e  w o r k  u n l e s s  I  a m  d e b u g g i n g  l o l ?  A m  I  n o t  a w a i t i n g  s m o m e t h i n g ? " +
        "> " + ExpectedCleanedPrompt + "\n";

    private static CleanedUserInputService CreateService(Mock<IRepository> repo)
    {
        repo.Setup(r => r.GetSessionLogChunksAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SessionLogChunkRecord>());

        return new(repo.Object, new TuiTextExtractor(), NullLogger<CleanedUserInputService>.Instance);
    }

    // ---- Bug 2: First row (from the split) picked the dropdown line as its cleaned text ----

    [Fact]
    public void CleanText_Row1FromSplit_IgnoresDropdownNoise_AndExtractsPromptContent()
    {
        var svc = CreateService(new Mock<IRepository>());

        // Row 1 raw (what InputAccumulator flushed prematurely when bracketed-paste closed):
        var row1Raw = "@ WaitingForUserInputObserver";

        var cleaned = svc.CleanText(row1Raw, DropdownAndPromptTui);

        Assert.Equal(ExpectedCleanedPrompt, cleaned);
    }

    // ---- Bug 3: Second row's clean got keystroke echo prepended to correct prompt ----

    [Fact]
    public void CleanText_Row2FromSplit_IgnoresKeystrokeEcho_AndExtractsPromptContent()
    {
        var svc = CreateService(new Mock<IRepository>());

        // Row 2 raw (the rest of the input after the premature flush; leading TAB
        // because that's what the InputAccumulator saw first after paste-close flush):
        var row2Raw = "\t why doesn't this code work unless I am debugging lol? Am I not awaiting something?";

        var cleaned = svc.CleanText(row2Raw, DropdownAndPromptTui);

        Assert.Equal(ExpectedCleanedPrompt, cleaned);
    }

    // ---- Bug 1: InputAccumulator flushes on bracketed-paste close even with pending buffer ----

    [Fact]
    public async Task InputAccumulator_BracketedPaste_WithPreExistingBuffer_DoesNotSplitIntoTwoRows()
    {
        // Reproduces the real byte sequence that caused the split: user typed a few
        // chars ("@ Wait"), then Claude Code delivered the selected file as a bracketed
        // paste (the rest of the partial path plus the expansion). The buffer at paste
        // close contains BOTH pre-paste typing AND paste content — it must NOT flush
        // until an explicit Enter arrives.
        const char ESC = '\x1B';
        var lines = new List<string>();
        await using var acc = new InputAccumulator(line =>
        {
            lock (lines) { lines.Add(line); }
            return Task.CompletedTask;
        });

        // "@ Wait" typed → bracketed paste of remaining chars → TAB → " why ..." → Enter
        acc.Append("@ Wait");
        acc.Append($"{ESC}[200~ingForUserInputObserver{ESC}[201~");
        acc.Append("\t why doesn't this code work unless I am debugging lol? Am I not awaiting something?\r");

        // Give the background worker a moment to drain
        await WaitUntilAsync(() => lines.Count >= 1);

        Assert.Single(lines);
        Assert.Equal(
            "@ WaitingForUserInputObserver\t why doesn't this code work unless I am debugging lol? Am I not awaiting something?",
            lines[0]);
    }

    // ---- Real session fixture: full end-to-end against the captured 349KB TUI log ----

    [Fact]
    public void CleanText_AgainstRealSessionFixture_Row1ExtractsPromptContent()
    {
        var tui = LoadFixture();
        var svc = CreateService(new Mock<IRepository>());

        // Matches the raw UserInputs row from the real session.
        var cleaned = svc.CleanText("@ WaitingForUserInputObserver", tui);

        Assert.Equal(ExpectedCleanedPrompt, cleaned);
    }

    [Fact]
    public void CleanText_AgainstRealSessionFixture_Row2ExtractsPromptContent()
    {
        var tui = LoadFixture();
        var svc = CreateService(new Mock<IRepository>());

        var cleaned = svc.CleanText(
            "\t why doesn't this code work unless I am debugging lol? Am I not awaiting something?",
            tui);

        Assert.Equal(ExpectedCleanedPrompt, cleaned);
    }

    // ---- `›` (U+203A) prompt glyph: Claude Code's chat history submission marker ----

    [Fact]
    public void CleanText_PromptGlyphU203A_IsStripped()
    {
        var svc = CreateService(new Mock<IRepository>());
        var tui = "\u203a Plan a fix for them!\n";

        var cleaned = svc.CleanText("Plan a fix for them!", tui);

        Assert.Equal("Plan a fix for them!", cleaned);
    }

    [Fact]
    public void CleanText_PromptGlyphU203A_WhenOnlySourceIsRawInput_IsStillStripped()
    {
        // Defense-in-depth: if no TUI output is available and the raw input itself
        // starts with `› ` (captured from an echo), InputEtlFilter should still strip it.
        var svc = CreateService(new Mock<IRepository>());

        var cleaned = svc.CleanText("\u203a Plan a fix for them!");

        Assert.Equal("Plan a fix for them!", cleaned);
    }

    private static string LoadFixture()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "Services", "CleanedInput", "Fixtures", "session_bf428817_log.bin");
        var bytes = File.ReadAllBytes(path);
        return Encoding.UTF8.GetString(bytes);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
    }
}
