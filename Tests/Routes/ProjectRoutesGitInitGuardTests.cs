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
    public void BlocksDevicePrefixedSpellingsOfAProtectedFolder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Device path prefixes are a Windows spelling.");

        Assert.True(IsDangerous(@"\\?\" + Profile));
        Assert.True(IsDangerous(@"\\.\" + Path.Combine(Profile, "Documents")));
        Assert.True(IsDangerous(@"\\?\C:\"));
        Assert.False(IsDangerous(@"\\?\" + Path.Combine(Profile, "source", "app")));
    }

    [Fact]
    public void BlocksALinkThatPointsAtAProtectedFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vb_git_init_link_{Guid.NewGuid():N}");
        var profile = Directory.CreateDirectory(Path.Combine(root, "Users", "tester")).FullName;
        Directory.CreateDirectory(Path.Combine(profile, "Documents"));
        var alias = Path.Combine(root, "alias");
        try
        {
            CreateDirectoryLink(alias, profile);

            Assert.True(ProjectRoutes.IsDangerousDirectory(alias, profile, []));
            Assert.True(ProjectRoutes.IsDangerousDirectory(Path.Combine(alias, "Documents"), profile, []));
            Assert.False(ProjectRoutes.IsDangerousDirectory(Path.Combine(alias, "Documents", "app"), profile, []));
        }
        finally
        {
            if (new DirectoryInfo(alias).LinkTarget is not null)
                Directory.Delete(alias);
            Directory.Delete(root, recursive: true);
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

    /// <summary>A symlink where permitted; on Windows a junction, which needs no privilege.</summary>
    private static void CreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is UnauthorizedAccessException or IOException)
        {
        }

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target })
            startInfo.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(startInfo)!;
        process.WaitForExit();
        if (process.ExitCode != 0)
            Assert.Skip("Could not create a directory link: " + process.StandardError.ReadToEnd());
    }
}
