using VibeRails.Services.VCA;
using Xunit;

namespace Tests.Services.VCA;

public class PathLockRuleTests
{
    [Theory]
    [InlineData("File Lock('src/app.cs')", PathLockKind.File, "src/app.cs")]
    [InlineData("file lock ( \"src/app.cs\" )", PathLockKind.File, "src/app.cs")]
    [InlineData("Directory Lock('src/generated')", PathLockKind.Directory, "src/generated")]
    public void TryParse_AcceptsCanonicalParameterizedSyntax(
        string text,
        PathLockKind expectedKind,
        string expectedPath)
    {
        Assert.True(PathLockRule.TryParse(text, out var specification));
        Assert.Equal(expectedKind, specification.Kind);
        Assert.Equal(expectedPath, specification.RelativePath);
    }

    [Theory]
    [InlineData("File Lock(src/app.cs)")]
    [InlineData("File Lock('')")]
    [InlineData("Directory Lock()")]
    [InlineData("Not a lock('src')")]
    public void TryParse_RejectsMalformedSyntax(string text)
    {
        Assert.False(PathLockRule.TryParse(text, out _));
    }

    /// <summary>
    /// A lock path is the one place arbitrary caller text reaches AGENTS.md — every other rule has
    /// to match a known name exactly. A line break in it would be written back as two lines, and a
    /// second line opening with '#' closes the rules section, dropping every rule below it from
    /// both the hook and the Rules page. Path.GetFullPath carries newlines through without
    /// complaint, so nothing downstream catches this; it has to fail to parse here.
    /// </summary>
    [Theory]
    [InlineData("File Lock('src\napp.cs')")]
    [InlineData("File Lock('src\n## Injected')")]
    [InlineData("File Lock('src\r\n## Injected')")]
    [InlineData("Directory Lock('src\n## Rules')")]
    [InlineData("File Lock('src\0app.cs')")]
    public void TryParse_RejectsLineBreaksAndNulInThePath(string text)
    {
        Assert.False(PathLockRule.TryParse(text, out _));

        // Still recognizably a lock, so the write path reports the syntax error rather than
        // silently dropping it as an unknown rule.
        Assert.True(PathLockRule.LooksLikePathLock(text));
    }

    [Fact]
    public void TryResolveRepositoryPath_UsesTheDeclaringAgentDirectory()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "path-lock-root"));
        var source = Path.Combine(root, "nested", "AGENTS.md");
        var specification = new PathLockSpecification(PathLockKind.File, @"config\settings.json");

        var resolved = PathLockRule.TryResolveRepositoryPath(
            specification,
            source,
            root,
            out var repositoryPath,
            out var error);

        Assert.True(resolved, error);
        Assert.Equal("nested/config/settings.json", repositoryPath);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("/absolute/file.txt")]
    [InlineData("C:\\absolute\\file.txt")]
    public void TryResolveRepositoryPath_RejectsAbsoluteAndEscapingPaths(string requestedPath)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "path-lock-root"));
        var source = Path.Combine(root, "nested", "AGENTS.md");

        var resolved = PathLockRule.TryResolveRepositoryPath(
            new PathLockSpecification(PathLockKind.File, requestedPath),
            source,
            root,
            out _,
            out var error);

        Assert.False(resolved);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryResolveRepositoryPath_RejectsLockingTheDeclaringAgentFile()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "path-lock-root"));
        var source = Path.Combine(root, "AGENTS.md");

        var resolved = PathLockRule.TryResolveRepositoryPath(
            new PathLockSpecification(PathLockKind.File, "AGENTS.md"),
            source,
            root,
            out _,
            out var error);

        Assert.False(resolved);
        Assert.Contains("own declaring AGENTS.md", error);
    }

    [Fact]
    public void Matches_FileLockChecksBothSidesOfARename()
    {
        var specification = new PathLockSpecification(PathLockKind.File, "locked.txt");

        Assert.True(PathLockRule.Matches(
            specification,
            "locked.txt",
            "moved.txt",
            "locked.txt",
            "AGENTS.md",
            StringComparison.Ordinal));
        Assert.False(PathLockRule.Matches(
            specification,
            "locked.txt",
            "other.txt",
            null,
            "AGENTS.md",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Matches_DirectoryLockIsRecursiveAndPathBoundaryAware()
    {
        var specification = new PathLockSpecification(PathLockKind.Directory, "src/locked");

        Assert.True(PathLockRule.Matches(
            specification, "src/locked", "src/locked/file.cs", null, "AGENTS.md", StringComparison.Ordinal));
        Assert.True(PathLockRule.Matches(
            specification, "src/locked", "src/locked/nested/file.cs", null, "AGENTS.md", StringComparison.Ordinal));
        Assert.False(PathLockRule.Matches(
            specification, "src/locked", "src/locked-old/file.cs", null, "AGENTS.md", StringComparison.Ordinal));
    }

    [Fact]
    public void Matches_DirectoryLockDoesNotLockItsOwnDeclaringAgent()
    {
        var specification = new PathLockSpecification(PathLockKind.Directory, ".");

        Assert.False(PathLockRule.Matches(
            specification, ".", "AGENTS.md", null, "AGENTS.md", StringComparison.Ordinal));
        Assert.True(PathLockRule.Matches(
            specification, ".", "src/app.cs", null, "AGENTS.md", StringComparison.Ordinal));
    }
}
