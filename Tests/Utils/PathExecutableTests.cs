using System.Text.RegularExpressions;
using VibeRails.Services.Git;
using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

/// <summary>
/// A program is found in the absolute directories on PATH and nowhere else (VIBE-310). A bare
/// name handed to the OS is looked up in the current directory, the opened project, before PATH.
/// </summary>
public sealed class PathExecutableTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vb-path-exe-" + Guid.NewGuid().ToString("N"));
    private readonly string _first;
    private readonly string _second;

    public PathExecutableTests()
    {
        _first = Directory.CreateDirectory(Path.Combine(_root, "first")).FullName;
        _second = Directory.CreateDirectory(Path.Combine(_root, "second dir")).FullName;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string Join(params string[] entries) => string.Join(Path.PathSeparator, entries);

    private static string Program(string directory, string name)
    {
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void FindsTheFirstMatchInAbsoluteEntries_IncludingQuotedOnes()
    {
        var name = OperatingSystem.IsWindows() ? "tool.exe" : "tool";
        Program(_second, name);
        var expected = Program(_first, name);

        Assert.Equal(expected, PathExecutable.Find("tool", Join(_first, _second), OperatingSystem.IsWindows()));
        Assert.Equal(Path.Combine(_second, name), PathExecutable.Find("tool", Join("\"" + _second + "\"", _first), OperatingSystem.IsWindows()));
        Assert.Null(PathExecutable.Find("absent", Join(_first, _second), OperatingSystem.IsWindows()));
    }

    [Fact]
    public void RelativeEntriesAndTheCurrentDirectoryAreNeverSearched()
    {
        // A file that exists in the current directory under the name being resolved.
        var name = "vb-probe-" + Guid.NewGuid().ToString("N");
        var current = Directory.GetCurrentDirectory();
        var planted = Path.Combine(current, OperatingSystem.IsWindows() ? name + ".exe" : name);
        File.WriteAllText(planted, "");
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(planted, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var viaParent = ".." + Path.DirectorySeparatorChar + Path.GetFileName(current);
            foreach (var searchPath in new[] { "", ".", "." + Path.DirectorySeparatorChar, Join(".", "bin", viaParent) })
                Assert.Null(PathExecutable.Find(name, searchPath, OperatingSystem.IsWindows()));

            var resolved = PathExecutable.Resolve(name);
            Assert.True(Path.IsPathFullyQualified(resolved));
            Assert.False(File.Exists(resolved), "a program that is not on PATH must resolve to a path that cannot start");
            Assert.NotEqual(Path.GetFullPath(planted), Path.GetFullPath(resolved));
        }
        finally
        {
            File.Delete(planted);
        }
    }

    [Fact]
    public void OnWindowsOnlyExeIsAcceptedUnlessScriptsAreAllowed()
    {
        File.WriteAllText(Path.Combine(_first, "tool.cmd"), "");
        File.WriteAllText(Path.Combine(_second, "tool.bat"), "");
        File.WriteAllText(Path.Combine(_second, "named.cmd"), "");

        Assert.Null(PathExecutable.Find("tool", Join(_first, _second), isWindows: true));
        Assert.Equal(Path.Combine(_first, "tool.cmd"), PathExecutable.Find("tool", Join(_first, _second), isWindows: true, allowScripts: true));
        Assert.Equal(Path.Combine(_second, "tool.bat"), PathExecutable.Find("tool", _second, isWindows: true, allowScripts: true));
        // A name that already carries its extension is taken as written.
        Assert.Equal(Path.Combine(_second, "named.cmd"), PathExecutable.Find("named.cmd", Join(_first, _second), isWindows: true));

        File.WriteAllText(Path.Combine(_second, "tool.exe"), "");
        Assert.Equal(Path.Combine(_second, "tool.exe"), PathExecutable.Find("tool", Join(_second, _first), isWindows: true, allowScripts: true));
    }

    [Theory]
    [InlineData("./git")]
    [InlineData("bin/git")]
    [InlineData(@"bin\git.exe")]
    [InlineData("/usr/bin/git")]
    [InlineData(@"C:\Program Files\Git\cmd\git.exe")]
    public void AValueThatNamesADirectoryIsLeftToItsCaller(string value)
    {
        Assert.Equal(value, PathExecutable.Resolve(value));
        Assert.Null(PathExecutable.Find(value, _first, OperatingSystem.IsWindows()));
    }

    [Fact]
    public void GitIsStartedByAbsolutePath()
    {
        var startInfo = GitCli.CreateStartInfo("", ["--version"]);

        Assert.True(Path.IsPathFullyQualified(startInfo.FileName), startInfo.FileName);
        Assert.Equal("git", Path.GetFileNameWithoutExtension(startInfo.FileName));
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void NoProcessIsStartedByABareProgramNameLiteral()
    {
        var root = FindRepositoryRoot();
        var separator = Path.DirectorySeparatorChar;
        // FileName = "name", new ProcessStartInfo("name"...), Process.Start("name"...), Cli.Wrap("name"):
        // a literal with no directory in it. Shell and terminal names held in constants are resolved
        // by their own launchers and are outside this sweep.
        // "const string FileName" declarations name data files, not programs.
        var bare = new Regex("""(?:(?<!const\s+string\s+)\bFileName\s*=\s*|\bnew\s+ProcessStartInfo\(\s*|\bProcess\.Start\(\s*|\bCli\.Wrap\(\s*)"(?![A-Za-z]:|/|\\)[^"/\\]*"\s*[,;)}\r\n]""");
        var offenders = new[] { "VibeRails", "VibeRails.Data.Sqlite", "TokenSaver", "PyBridge", "MintLint" }
            .SelectMany(project => Directory.EnumerateFiles(Path.Combine(root, project), "*.cs", SearchOption.AllDirectories))
            .Where(path => !path.Contains($"{separator}bin{separator}") && !path.Contains($"{separator}obj{separator}"))
            .SelectMany(path => bare.Matches(File.ReadAllText(path)).Select(match => $"{Path.GetRelativePath(root, path)}: {match.Value.Trim()}"))
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Start programs through PathExecutable.Resolve (or an absolute system path), never a bare name:\n" + string.Join("\n", offenders));
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VibeRails.slnx")))
                return directory.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }
}
