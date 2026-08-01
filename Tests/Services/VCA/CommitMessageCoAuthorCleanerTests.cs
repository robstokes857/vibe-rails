using System.Text.Json;
using VibeRails.Services.VCA.Hooks;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.VCA;

public sealed class CommitMessageCoAuthorCleanerTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"vca_coauthor_{Guid.NewGuid():N}");

    public CommitMessageCoAuthorCleanerTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void Settings_DefaultsToRemovingCoAuthorTrailers()
    {
        Assert.True(new Settings().RemoveCoAuthorTrailers);

        var settingsFromOlderFile = JsonSerializer.Deserialize(
            "{}",
            ConfigJsonContext.Default.Settings);
        Assert.NotNull(settingsFromOlderFile);
        Assert.True(settingsFromOlderFile.RemoveCoAuthorTrailers);
    }

    [Fact]
    public async Task RemoveAsync_Enabled_RemovesAllTrailerCasingsAndKeepsOtherContent()
    {
        var path = Path.Combine(_tempDirectory, "COMMIT_EDITMSG");
        const string original =
            "Implement feature\r\n\r\n" +
            "Body mentions Co-authored-by: in prose.\r\n\r\n" +
            "Co-authored-by: Claude <noreply@anthropic.com>\r\n" +
            "\tCO-AUTHORED-BY : Codex <codex@openai.com>\r\n" +
            "Signed-off-by: Developer <dev@example.com>\r\n";
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        var cleaner = new CommitMessageCoAuthorCleaner(() => true);

        var removed = await cleaner.RemoveAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(2, removed);
        Assert.Equal(
            "Implement feature\r\n\r\n" +
            "Body mentions Co-authored-by: in prose.\r\n\r\n" +
            "Signed-off-by: Developer <dev@example.com>\r\n",
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveAsync_Disabled_LeavesCommitMessageUnchanged()
    {
        var path = Path.Combine(_tempDirectory, "COMMIT_EDITMSG-disabled");
        const string original =
            "Implement feature\n\nCo-authored-by: Claude <noreply@anthropic.com>\n";
        await File.WriteAllTextAsync(path, original, TestContext.Current.CancellationToken);
        var cleaner = new CommitMessageCoAuthorCleaner(() => false);

        var removed = await cleaner.RemoveAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.Equal(
            original,
            await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RemoveTrailers_RemovesOrphanedBlankLineAtEndButKeepsComments()
    {
        const string original =
            "Implement feature\n\n" +
            "# Co-authored-by: example from the commit template\n" +
            "Co-authored-by: Claude <noreply@anthropic.com>\n\n";

        var cleaned = CommitMessageCoAuthorCleaner.RemoveTrailers(original, out var removed);

        Assert.Equal(1, removed);
        Assert.Equal(
            "Implement feature\n\n# Co-authored-by: example from the commit template\n",
            cleaned);
    }

    [Fact]
    public void RemoveTrailers_KeepsABodyParagraphThatOpensWithTheToken()
    {
        // The setting promises to remove trailers. A trailer block is terminal, so a paragraph in
        // the body that happens to start with the token is prose — deleting it silently rewrites
        // what the author wrote about the very feature they are describing.
        const string original =
            "Fix the attribution bug\n\n" +
            "Co-authored-by: is the trailer GitHub reads for attribution, and we were\n" +
            "deleting it from body text as well as from the trailer block.\n\n" +
            "Co-authored-by: Claude <noreply@anthropic.com>\n";

        var cleaned = CommitMessageCoAuthorCleaner.RemoveTrailers(original, out var removed);

        Assert.Equal(1, removed);
        Assert.Equal(
            "Fix the attribution bug\n\n" +
            "Co-authored-by: is the trailer GitHub reads for attribution, and we were\n" +
            "deleting it from body text as well as from the trailer block.\n",
            cleaned);
    }

    [Fact]
    public void RemoveTrailers_SingleParagraphMessageHasNoTrailerBlock()
    {
        // Git's rule, and the reason the body case above is even decidable: the first paragraph is
        // the description, so a message with no blank line in it has no trailers at all.
        const string original = "Co-authored-by: Claude <noreply@anthropic.com>\n";

        var cleaned = CommitMessageCoAuthorCleaner.RemoveTrailers(original, out var removed);

        Assert.Equal(0, removed);
        Assert.Equal(original, cleaned);
    }

    [Fact]
    public void RemoveTrailers_TakesTheTrailerOutOfAnAiFooterAndLeavesTheRestOfIt()
    {
        // The shape this policy exists for, in its awkward form: the generated-with line sits
        // directly above the trailer with no blank line, so the run of trailer lines at the end is
        // just the one. The generated-with line is not a trailer and is not this setting's business.
        const string original =
            "Add the pause endpoint\n\n" +
            "\U0001F916 Generated with [Claude Code](https://claude.com/claude-code)\n" +
            "Co-authored-by: Claude <noreply@anthropic.com>\n";

        var cleaned = CommitMessageCoAuthorCleaner.RemoveTrailers(original, out var removed);

        Assert.Equal(1, removed);
        Assert.Equal(
            "Add the pause endpoint\n\n" +
            "\U0001F916 Generated with [Claude Code](https://claude.com/claude-code)\n",
            cleaned);
    }

    [Fact]
    public void RemoveTrailers_TakesWrappedContinuationLinesWithTheirTrailer()
    {
        // Git folds an indented line into the value above it. Removing the trailer without its
        // continuation would strand the tail of a co-author's address as a line of its own.
        const string original =
            "Implement it\n\n" +
            "Co-authored-by: A Very Long Name\n" +
            "    <verylong@example.com>\n" +
            "Signed-off-by: Developer <dev@example.com>\n";

        var cleaned = CommitMessageCoAuthorCleaner.RemoveTrailers(original, out var removed);

        Assert.Equal(1, removed);
        Assert.Equal(
            "Implement it\n\nSigned-off-by: Developer <dev@example.com>\n",
            cleaned);
    }

    [Fact]
    public void RemoveTrailers_IgnoresEverythingBelowTheScissorsLine()
    {
        // `git commit --verbose` appends an UNCOMMENTED diff below the scissors, and the hook sees
        // the raw file. Without the cut, that diff is the message's final paragraph — so the real
        // trailer would be out of scope while any Co-authored-by line inside the diff was in it.
        const string original =
            "Fix the thing\n\n" +
            "Co-authored-by: Claude <noreply@anthropic.com>\n\n" +
            "# ------------------------ >8 ------------------------\n" +
            "# Do not modify or remove the line above.\n" +
            "diff --git a/x b/x\n" +
            "@@ -1 +1 @@\n" +
            "-Co-authored-by: was here\n" +
            "+Co-authored-by: still here\n";

        var cleaned = CommitMessageCoAuthorCleaner.RemoveTrailers(original, out var removed);

        Assert.Equal(1, removed);
        Assert.DoesNotContain("Co-authored-by: Claude", cleaned, StringComparison.Ordinal);
        Assert.Contains("-Co-authored-by: was here", cleaned, StringComparison.Ordinal);
        Assert.Contains("+Co-authored-by: still here", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveAsync_PreservesTheByteOrderMarkAndNonAsciiBytesItDidNotTouch()
    {
        // The message file is written in i18n.commitEncoding, which this class never learns. It
        // edits bytes rather than re-encoding text, so a BOM and any multi-byte characters survive
        // exactly. Reading the file as text would rewrite the whole message as BOM-less UTF-8.
        var path = Path.Combine(_tempDirectory, "COMMIT_EDITMSG-bom");
        var preamble = System.Text.Encoding.UTF8.GetPreamble();
        var original = Concat(
            preamble,
            System.Text.Encoding.UTF8.GetBytes(
                "Fix the café bug\n\nCo-authored-by: Claude <noreply@anthropic.com>\n"));
        var expected = Concat(preamble, System.Text.Encoding.UTF8.GetBytes("Fix the café bug\n"));
        await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);
        var cleaner = new CommitMessageCoAuthorCleaner(() => true);

        var removed = await cleaner.RemoveAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(1, removed);
        Assert.Equal(
            expected,
            await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RemoveAsync_SkipsUtf16FilesRatherThanCorruptingThem()
    {
        // The byte round trip holds for every ASCII-compatible encoding, which is all of them in
        // practice — but not UTF-16, where writing back through it would mangle the message.
        // Removing a trailer is not worth that, so the file is left exactly as found.
        var path = Path.Combine(_tempDirectory, "COMMIT_EDITMSG-utf16");
        var original = Concat(
            System.Text.Encoding.Unicode.GetPreamble(),
            System.Text.Encoding.Unicode.GetBytes(
                "Fix the thing\n\nCo-authored-by: Claude <noreply@anthropic.com>\n"));
        await File.WriteAllBytesAsync(path, original, TestContext.Current.CancellationToken);
        var cleaner = new CommitMessageCoAuthorCleaner(() => true);

        var removed = await cleaner.RemoveAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(0, removed);
        Assert.Equal(
            original,
            await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void ReadEnabledSetting_FailsClosedWhenTheSettingCannotBeRead()
    {
        // Deliberately the opposite of the documented default. This is the path where the choice is
        // unknown, and the operation it guards rewrites the author's message irreversibly: skipping
        // cleanup for one commit is recoverable, rewriting for someone who switched it off is not.
        Assert.False(CommitMessageCoAuthorCleaner.ReadEnabledSetting(
            () => throw new IOException("settings.json is locked")));

        Assert.True(CommitMessageCoAuthorCleaner.ReadEnabledSetting(() => true));
        Assert.False(CommitMessageCoAuthorCleaner.ReadEnabledSetting(() => false));
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        var result = new byte[first.Length + second.Length];
        first.CopyTo(result, 0);
        second.CopyTo(result, first.Length);
        return result;
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }
}
