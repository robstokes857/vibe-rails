using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

/// <summary>
/// The slug bridges two namespaces that do not accept the same characters: an environment name
/// may contain spaces, a workspace name becomes a directory and a git branch and may not.
/// Slugging is lossy, so the environment id — not the slugged text — is what carries identity.
/// </summary>
public sealed class WorkspaceNameSlugTests
{
    private const string RunToken = "abcdef01";
    private static readonly DateTime Timestamp = new(2026, 8, 7, 14, 30, 5, DateTimeKind.Utc);

    [Theory]
    [InlineData("nightly", "nightly-e42")]
    [InlineData("Nightly Review", "Nightly-Review-e42")]
    [InlineData("Nightly   Review", "Nightly-Review-e42")]
    [InlineData("  padded  ", "padded-e42")]
    [InlineData("keeps_underscores-and-hyphens", "keeps_underscores-and-hyphens-e42")]
    public void ForEnvironment_ProducesASandboxSafeName(string environmentName, string expected)
    {
        Assert.Equal(expected, WorkspaceNameSlug.ForEnvironment(environmentName, 42));
    }

    [Theory]
    [InlineData("Nightly Review")]
    [InlineData("under_score")]
    [InlineData("dash-name")]
    [InlineData("Trailing ")]
    public void ForEnvironment_MatchesTheSandboxNameCharset(string environmentName)
    {
        var slug = WorkspaceNameSlug.ForEnvironment(environmentName, 7);

        // Mirrors SandboxService.ValidNameRegex. A slug that fails here would be rejected by
        // sandbox creation at launch time, which is far too late to find out.
        Assert.Matches("^[a-zA-Z0-9_][a-zA-Z0-9_-]*$", slug);
        Assert.True(slug.Length <= WorkspaceNameSlug.MaxLength);
    }

    [Fact]
    public void ForEnvironment_IsStable()
    {
        // Persistent workspaces are found again by re-deriving the name. If this ever varied
        // per call, every launch would clone instead of reusing.
        Assert.Equal(
            WorkspaceNameSlug.ForEnvironment("Nightly Review", 42),
            WorkspaceNameSlug.ForEnvironment("Nightly Review", 42));
    }

    [Fact]
    public void ForEnvironment_SeparatesEnvironmentsWhoseNamesSlugAlike()
    {
        // The workspace root is a flat, global sandboxes/{name}. Two environments landing on
        // one directory would fight over it — "Nightly Review" and "Nightly-Review" slug to
        // identical text, so only the id keeps them apart.
        Assert.NotEqual(
            WorkspaceNameSlug.ForEnvironment("Nightly Review", 1),
            WorkspaceNameSlug.ForEnvironment("Nightly-Review", 2));
    }

    [Fact]
    public void ForEnvironment_FallsBackWhenNothingSurvives()
    {
        Assert.Equal("workspace-e3", WorkspaceNameSlug.ForEnvironment("   ", 3));
        Assert.Equal("workspace-e3", WorkspaceNameSlug.ForEnvironment(null, 3));
    }

    [Fact]
    public void ForEnvironment_TruncatesToTheCapWithoutTrailingSeparator()
    {
        var slug = WorkspaceNameSlug.ForEnvironment(new string('a', 40) + " " + new string('b', 40), 999999);

        Assert.True(slug.Length <= WorkspaceNameSlug.MaxLength);
        Assert.EndsWith("-e999999", slug, StringComparison.Ordinal);
        Assert.DoesNotContain("--", slug, StringComparison.Ordinal);
    }

    [Fact]
    public void ForRun_AppendsIdTimestampAndToken()
    {
        var runName = WorkspaceNameSlug.ForRun("Nightly Review", 42, Timestamp, RunToken);

        Assert.Equal("Nightly-Review-e42-20260807-143005-abcdef01", runName);
        Assert.True(runName.Length <= WorkspaceNameSlug.MaxLength);
        Assert.Matches("^[a-zA-Z0-9_][a-zA-Z0-9_-]*$", runName);
    }

    [Fact]
    public void ForRun_StaysUnderTheCapForAMaximumLengthName()
    {
        var runName = WorkspaceNameSlug.ForRun(
            new string('a', WorkspaceNameSlug.MaxLength),
            999999,
            Timestamp,
            RunToken);

        Assert.True(
            runName.Length <= WorkspaceNameSlug.MaxLength,
            $"Run name '{runName}' is {runName.Length} characters, over the {WorkspaceNameSlug.MaxLength} cap.");
    }

    [Fact]
    public void ForRun_DistinguishesRunsInsideTheSameSecond()
    {
        // A timestamp alone has one-second precision. Two runs starting together — which is
        // exactly what a burst of automations does — would otherwise target one directory.
        var first = WorkspaceNameSlug.ForRun("nightly", 1, Timestamp, WorkspaceNameSlug.NewRunToken());
        var second = WorkspaceNameSlug.ForRun("nightly", 1, Timestamp, WorkspaceNameSlug.NewRunToken());

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void NewRunToken_IsPathAndRefSafe()
    {
        for (var attempt = 0; attempt < 50; attempt++)
            Assert.Matches("^[0-9a-f]+$", WorkspaceNameSlug.NewRunToken());
    }

    [Fact]
    public void IsRunNameFor_RecognisesOnlyThisEnvironmentsRunNames()
    {
        var runName = WorkspaceNameSlug.ForRun("Nightly Review", 42, Timestamp, RunToken);

        Assert.True(WorkspaceNameSlug.IsRunNameFor("Nightly Review", 42, runName));

        // The persistent workspace for the same environment must NOT look like a run name, or
        // switching an environment to per-run would prune the clone holding the user's work.
        Assert.False(WorkspaceNameSlug.IsRunNameFor(
            "Nightly Review", 42, WorkspaceNameSlug.ForEnvironment("Nightly Review", 42)));

        // Same slugged text, different environment: retention must not cross that line.
        Assert.False(WorkspaceNameSlug.IsRunNameFor("Nightly-Review", 7, runName));

        Assert.False(WorkspaceNameSlug.IsRunNameFor("Other Env", 42, runName));
        Assert.False(WorkspaceNameSlug.IsRunNameFor("Nightly Review", 42, "hand-made-sandbox"));
        Assert.False(WorkspaceNameSlug.IsRunNameFor("Nightly Review", 42, null));
    }

    [Fact]
    public void IsRunNameFor_RejectsAPrefixWithoutTheRunSuffix()
    {
        // A hand-made sandbox that merely starts with the environment's stem is not a run.
        Assert.False(WorkspaceNameSlug.IsRunNameFor("nightly", 42, "nightly-e42"));
        Assert.False(WorkspaceNameSlug.IsRunNameFor("nightly", 42, "nightly-e42-notatimestamp"));
        Assert.False(WorkspaceNameSlug.IsRunNameFor("nightly", 42, "nightly-e421-20260807-143005-abcdef01"));
    }
}
