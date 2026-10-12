using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

public sealed class ProjectPathComparerTests
{
    [Fact]
    public void Matches_IgnoresTrailingSeparators()
    {
        var withSeparator = Path.Combine(Path.GetTempPath(), "project") + Path.DirectorySeparatorChar;
        var withoutSeparator = Path.Combine(Path.GetTempPath(), "project");

        // The launch directory, a git root, and a stored row spell the same path differently;
        // comparing raw makes an environment vanish from its own project.
        Assert.True(ProjectPathComparer.Matches(withSeparator, withoutSeparator));
    }

    [Fact]
    public void Matches_FollowsTheHostFilesystemForCase()
    {
        var upper = Path.Combine(Path.GetTempPath(), "Project", "App");
        var lower = Path.Combine(Path.GetTempPath(), "project", "app");

        // Windows and the default macOS filesystem fold case, so these are one project there.
        // On Linux they are genuinely different directories, and treating them as the same
        // would let one project read or reuse another's environments and workspaces.
        var expected = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        Assert.Equal(expected, ProjectPathComparer.Matches(upper, lower));
        Assert.Equal(expected, ProjectPathComparer.IsVisibleIn(upper, lower));
    }

    [Fact]
    public void Matches_IsAlwaysTrueForTheSameSpelling()
    {
        var path = Path.Combine(Path.GetTempPath(), "Project", "App");

        Assert.True(ProjectPathComparer.Matches(path, path));
    }

    [Fact]
    public void Matches_DistinguishesDifferentProjects()
    {
        Assert.False(ProjectPathComparer.Matches(@"C:\source\app", @"C:\source\other"));
    }

    [Fact]
    public void Matches_TreatsBlankAsNoMatch()
    {
        // "Unscoped" is a different state from "same project".
        Assert.False(ProjectPathComparer.Matches(null, null));
        Assert.False(ProjectPathComparer.Matches("", @"C:\source\app"));
        Assert.False(ProjectPathComparer.Matches(@"C:\source\app", "   "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsVisibleIn_HidesUnscopedEnvironments(string? projectPath)
    {
        Assert.False(ProjectPathComparer.IsVisibleIn(projectPath, @"C:\source\app"));
        Assert.False(ProjectPathComparer.IsVisibleIn(projectPath, @"C:\source\other"));
        Assert.False(ProjectPathComparer.IsVisibleIn(projectPath, null));
    }

    [Fact]
    public void IsVisibleIn_HidesEnvironmentsFromOtherProjects()
    {
        Assert.True(ProjectPathComparer.IsVisibleIn(@"C:\source\app", @"C:\source\app"));
        Assert.False(ProjectPathComparer.IsVisibleIn(@"C:\source\app", @"C:\source\other"));
    }
}
