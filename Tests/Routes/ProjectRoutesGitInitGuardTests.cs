using VibeRails.Routes;
using Xunit;

namespace Tests.Routes;

/// <summary>
/// Pins the <c>POST /api/v1/git/init</c> directory guard. Initializing runs <c>git add .</c>
/// on the launch directory, so a profile root or one of its personal folders would stage keys,
/// documents and app data. VIBE-194 (scan finding L-23).
/// </summary>
public sealed class ProjectRoutesGitInitGuardTests
{
    private static readonly string Profile = OperatingSystem.IsWindows()
        ? @"C:\Users\tester"
        : "/home/tester";

    private static bool IsDangerous(string path, params string?[] knownFolders) =>
        ProjectRoutes.IsDangerousDirectory(path, Profile, knownFolders);

    [Theory]
    [InlineData("")]
    [InlineData("Desktop")]
    [InlineData("Documents")]
    [InlineData("Downloads")]
    [InlineData("AppData")]
    [InlineData("AppData/Roaming")]
    [InlineData(".ssh")]
    [InlineData(".config")]
    [InlineData(".vibe_rails")]
    public void BlocksTheProfileRootAndItsWellKnownFolders(string child)
    {
        var path = child.Length == 0 ? Profile : Path.Combine(Profile, child);

        Assert.True(IsDangerous(path));
        Assert.True(IsDangerous(path + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void BlocksAnotherUsersProfileRoot()
    {
        var sibling = Path.Combine(Path.GetDirectoryName(Profile)!, "someone-else");

        Assert.True(IsDangerous(sibling));
        Assert.False(IsDangerous(Path.Combine(sibling, "project")));
    }

    [Fact]
    public void BlocksRedirectedKnownFolders()
    {
        var redirected = OperatingSystem.IsWindows()
            ? @"D:\OneDrive\Documents"
            : "/data/tester/Documents";

        Assert.True(IsDangerous(redirected, redirected, null, ""));
    }

    [Theory]
    [InlineData("source/my-app")]
    [InlineData("Documents/my-app")]
    [InlineData("Desktop/scratch/repo")]
    public void AllowsProjectFoldersInsideTheProfile(string relative)
    {
        Assert.False(IsDangerous(Path.Combine(Profile, relative)));
    }

    [Fact]
    public void BlocksSystemDirectoriesAndRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.True(IsDangerous(@"C:\"));
            Assert.True(IsDangerous(@"D:\"));
            Assert.True(IsDangerous(@"C:\Users"));
            Assert.True(IsDangerous(@"C:\Windows"));
            Assert.False(IsDangerous(@"C:\source\project"));
        }
        else
        {
            Assert.True(IsDangerous("/"));
            Assert.True(IsDangerous("/home"));
            Assert.True(IsDangerous("/Users"));
            Assert.False(IsDangerous("/srv/project"));
        }
    }

    [Fact]
    public void AProfileDirectlyUnderTheRootDoesNotBlockOtherTopLevelDirectories()
    {
        var rootProfile = OperatingSystem.IsWindows() ? @"C:\root" : "/root";
        var topLevel = OperatingSystem.IsWindows() ? @"C:\work" : "/work";

        Assert.True(ProjectRoutes.IsDangerousDirectory(rootProfile, rootProfile, []));
        Assert.False(ProjectRoutes.IsDangerousDirectory(topLevel, rootProfile, []));
    }
}
