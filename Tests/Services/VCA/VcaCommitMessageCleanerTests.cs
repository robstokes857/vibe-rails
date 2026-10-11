using VibeRails.Services.VCA.Hooks;
using Xunit;

namespace Tests.Services.VCA;

/// <summary>
/// The commit-msg hook runs <c>git stripspace --strip-comments</c> through the shared launch
/// contract. The message must round-trip byte-exact, so stdin and stdout both speak UTF-8 and no
/// byte-order mark is prepended.
/// </summary>
public sealed class VcaCommitMessageCleanerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-stripspace-{Guid.NewGuid():N}");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public VcaCommitMessageCleanerTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task StripCommentsAsync_RemovesCommentLines_AndKeepsTheRest()
    {
        var cleaned = await VcaCommitMessageCleaner.StripCommentsAsync(
            "Fix the thing\n\n# Please enter the commit message\n# Lines starting with '#' are ignored\nBody line\n",
            _root, Ct);

        Assert.Equal("Fix the thing\n\nBody line\n", cleaned);
    }

    [Fact]
    public async Task StripCommentsAsync_RoundTripsNonAsciiText_WithoutABom()
    {
        const string message = "Ünïcödé — ✓ 日本語 emoji 🚀\n";

        var cleaned = await VcaCommitMessageCleaner.StripCommentsAsync(message, _root, Ct);

        Assert.Equal(message, cleaned);
        Assert.False(cleaned.StartsWith('﻿'), "a byte-order mark leaked into the commit message");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
