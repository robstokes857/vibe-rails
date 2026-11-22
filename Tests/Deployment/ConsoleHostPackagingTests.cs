using VibeRails.Services.Terminal;
using Xunit;
using ConPtyApi = Pty.Net.Windows.ConPtyApi;

namespace Tests.Deployment;

/// <summary>
/// VIBE-354: the modern Windows console host is two files, <c>conpty\conpty.dll</c> and
/// <c>conpty\OpenConsole.exe</c>, and it only works as a pair in one folder. Four places have to
/// agree on that or a release ships a setting that silently does nothing: the project that
/// copies them, the VSIX staging script, the installer's closed list of files, and the release
/// archive check. The last two run only in CI, so a normal test run has to catch a mismatch.
/// </summary>
public sealed class ConsoleHostPackagingTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void TheNamesTheLoaderLooksForAreTheNamesThatShip()
    {
        Assert.Equal("conpty", BundledConsoleHost.DirectoryName);
        Assert.Equal("conpty.dll", BundledConsoleHost.LibraryName);
        Assert.Equal("OpenConsole.exe", BundledConsoleHost.HostName);
        // conpty.dll starts the host by this name, from its own folder.
        Assert.Equal(BundledConsoleHost.HostName, ConPtyApi.HostExecutableName);
    }

    [Fact]
    public void TheProjectCopiesBothFilesIntoOneFolderForBuildAndPublish()
    {
        var project = ReadWithLf("VibeRails/VibeRails.csproj");

        // Only the files are taken from the package. Its own targets place OpenConsole.exe in an
        // x64\ subfolder, and only when PlatformTarget names an architecture.
        Assert.Contains("<PackageReference Include=\"Microsoft.Windows.Console.ConPTY\"", project);
        Assert.Contains("GeneratePathProperty=\"true\" ExcludeAssets=\"all\"", project);
        foreach (var (source, link) in new[]
                 {
                     (@"runtimes\win-x64\native\conpty.dll", @"conpty\conpty.dll"),
                     (@"build\native\runtimes\x64\OpenConsole.exe", @"conpty\OpenConsole.exe"),
                 })
        {
            Assert.Contains(
                $"<Content Include=\"$(PkgMicrosoft_Windows_Console_ConPTY)\\{source}\" Link=\"{link}\" "
                + "CopyToOutputDirectory=\"PreserveNewest\" CopyToPublishDirectory=\"PreserveNewest\"",
                project);
        }
    }

    [Fact]
    public void TheExtensionPackageStagesThePairAndFailsWithoutIt()
    {
        var script = ReadWithLf("deploy/prepare-binaries.ps1");

        // The script copies top-level publish files only, so a subfolder must be named.
        Assert.Contains("$consoleHostSource = Join-Path $sourceDir \"conpty\"", script);
        Assert.Contains("foreach ($consoleHostName in @(\"conpty.dll\", \"OpenConsole.exe\"))", script);
        Assert.Contains("throw \"Console host file conpty/$consoleHostName is missing from $sourceDir.", script);
    }

    [Fact]
    public void TheInstallerAcceptsExactlyThePair()
    {
        var script = ReadWithLf("deploy/build-installer.ps1");

        // The installer refuses any file it does not know ("Unrecognized release file"), so a
        // publish folder with the host in it fails the release unless both names are listed.
        Assert.Contains("$consoleHost = @('conpty.dll', 'OpenConsole.exe')", script);
        Assert.Contains(
            "elseif ($relative -imatch '^conpty\\\\([^\\\\]+)$' -and $consoleHost -icontains $Matches[1]) { }", script);
        // The fixture the installer workflow builds has to carry them, or that branch never runs.
        Assert.Contains("'conpty/conpty.dll', 'conpty/OpenConsole.exe')", ReadWithLf(".github/workflows/installer.yml"));
    }

    [Fact]
    public void TheReleaseArchiveMustContainThePair()
    {
        var workflow = ReadWithLf(".github/workflows/release.yml");

        Assert.Contains("\"conpty/conpty.dll\",\n", workflow);
        Assert.Contains("\"conpty/OpenConsole.exe\"\n", workflow);
    }

    private static string ReadWithLf(string relativePath) =>
        File.ReadAllText(Path.Combine(RepositoryRoot, relativePath)).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VibeRails.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("VibeRails.slnx was not found above the test output directory.");
    }
}
