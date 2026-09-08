using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

public sealed class VsCodeLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vscode-launch-" + Guid.NewGuid().ToString("N"));

    public VsCodeLauncherTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void FileLaunch_PassesWorkspaceAndExactFilenameWithoutShellInterpretation()
    {
        var directory = Path.Combine(_root, "rules & notes (draft)");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "vc.rules.md");
        File.WriteAllText(path, "# Rules");
        var info = VsCodeLauncher.CreateStartInfo(_root, path, "code-test");
        Assert.False(info.UseShellExecute);
        Assert.Equal(new[] { "--new-window", _root, "--goto", path }, info.ArgumentList);
        Assert.Equal(_root, info.WorkingDirectory);
    }

    [Fact]
    public void NoFile_StillOpensProject()
    {
        var info = VsCodeLauncher.CreateStartInfo(_root, null, "code-test");
        Assert.Equal(new[] { "--new-window", _root }, info.ArgumentList);
    }

    [Theory]
    [InlineData("../outside/vc.rules.md")]
    [InlineData("missing.md")]
    [InlineData("//server/share/vc.rules.md")]
    [InlineData("\\\\server\\share\\vc.rules.md")]
    [InlineData(".")]
    public void InvalidTarget_IsRejectedBeforeLaunching(string target)
    {
        Assert.Throws<ArgumentException>(() => VsCodeLauncher.CreateStartInfo(_root, target, "code-test"));
    }

    [Fact]
    public void ExistingFileOutsideProject_IsRejectedAtTheDirectoryBoundary()
    {
        var project = Path.Combine(_root, "project");
        var sibling = Path.Combine(_root, "project-other");
        Directory.CreateDirectory(project);
        Directory.CreateDirectory(sibling);
        var outside = Path.Combine(sibling, "vc.rules.md");
        File.WriteAllText(outside, "# Outside policy");
        Assert.Throws<ArgumentException>(() => VsCodeLauncher.CreateStartInfo(project, outside, "code-test"));
        Assert.Throws<ArgumentException>(() => VsCodeLauncher.CreateStartInfo(project, "../project-other/vc.rules.md", "code-test"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
