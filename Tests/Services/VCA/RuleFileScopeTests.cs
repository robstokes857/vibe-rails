using VibeRails.Services.VCA;
using Xunit;

namespace Tests.Services.VCA;

public sealed class RuleFileScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rule-scope-{Guid.NewGuid():N}");

    public RuleFileScopeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ParentScope_IncludesNestedPoliciesAndTheirFiles()
    {
        var source = Write("vc.rules.md");
        Write("app.cs");
        Write("nested/vc.rules.md");
        Write("nested/app.cs");
        Write("nested/deep/data.json");
        Write("nested-other/app.cs");

        var files = RuleFileScope.ListFiles(source, TestContext.Current.CancellationToken);

        Assert.Equal(new[]
        {
            "app.cs", "nested-other/app.cs", "nested/app.cs", "nested/deep/data.json", "nested/vc.rules.md"
        }, files);
    }

    [Fact]
    public void NestedScope_ExcludesParentsAndSiblingPrefixMatches()
    {
        Write("vc.rules.md");
        var source = Write("nested/vc.rules.md");
        Write("nested/app.cs");
        Write("nested/child/vc.rules.md");
        Write("nested-other/app.cs");
        Write("outside.txt");

        Assert.Equal(new[] { "app.cs", "child/vc.rules.md" }, RuleFileScope.ListFiles(source, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Scope_PrunesGitMetadataButKeepsHiddenAndExtensionlessProjectFiles()
    {
        var source = Write("vc.rules.md");
        Write(".git/objects/object");
        Write(".git/config");
        Write("nested/.git");
        Write(".gitignore");
        Write(".github/workflows/build.yml");
        Write("LICENSE");
        var hidden = Write(".config/settings");
        if (OperatingSystem.IsWindows())
            File.SetAttributes(hidden, File.GetAttributes(hidden) | FileAttributes.Hidden);

        Assert.Equal(new[] { ".config/settings", ".github/workflows/build.yml", ".gitignore", "LICENSE" },
            RuleFileScope.ListFiles(source, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Scope_DoesNotTraverseLinkedDirectories()
    {
        var source = Write("project/vc.rules.md");
        Write("project/app.cs");
        Write("outside/private.txt");
        var link = Path.Combine(_root, "project", "linked");
        try
        {
            Directory.CreateSymbolicLink(link, Path.Combine(_root, "outside"));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            Assert.Skip($"Directory symlinks are unavailable: {exception.Message}");
        }

        try
        {
            Assert.Equal(new[] { "app.cs" }, RuleFileScope.ListFiles(source, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(link);
        }
    }

    [Fact]
    public void Scope_RejectsAFileThatIsNotAPolicy()
    {
        Assert.Throws<ArgumentException>(() => RuleFileScope.ListFiles(Write("notes.md"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Scope_HonorsCancellation()
    {
        var source = Write("vc.rules.md");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => RuleFileScope.ListFiles(source, cancellation.Token));
    }

    private string Write(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "test");
        return path;
    }

    public void Dispose()
    {
        var fullPath = Path.GetFullPath(_root);
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (!fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Refusing to remove a test directory outside the temporary directory.");
        Directory.Delete(fullPath, recursive: true);
    }
}
