using System.Diagnostics;
using System.Text.RegularExpressions;
using VibeRails.Services.Git;
using Xunit;

namespace Tests.Services.Git;

public sealed class GitCliTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-gitcli-{Guid.NewGuid():N}");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public GitCliTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task Timeout_ReturnsWithoutHangingOnStreamDrain()
    {
        Assert.True((await GitCli.RunAsync(_root, ["init"], Ct)).Succeeded, "git init");
        var sw = Stopwatch.StartNew();
        // cat-file --batch waits on stdin. If kill fails, the old drain awaited EOF on
        // the original token and the board request stuck; DrainBound must finish this.
        var result = await GitCli.RunAsync(_root, ["cat-file", "--batch"], Ct, timeout: TimeSpan.FromMilliseconds(150));
        sw.Stop();

        // Hang prevention is the assertion. cat-file may exit immediately if stdin is already EOF.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"GitCli hung for {sw.Elapsed}");
        Assert.NotNull(result);
    }

    [Fact]
    public async Task SuccessfulCommand_StillReturnsOutput()
    {
        Assert.True((await GitCli.RunAsync(_root, ["init"], Ct)).Succeeded, "git init");
        var result = await GitCli.RunAsync(_root, ["rev-parse", "--is-inside-work-tree"], Ct);
        Assert.True(result.Succeeded, result.StdErr);
        Assert.Equal("true", result.StdOut.Trim());
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_RunsGitInTheGivenDirectory_NotTheHostsCurrentDirectory()
    {
        Assert.True((await GitCli.RunAsync(_root, ["init"], Ct)).Succeeded, "git init");
        var sub = Directory.CreateDirectory(Path.Combine(_root, "sub", "deeper")).FullName;
        await File.WriteAllTextAsync(Path.Combine(sub, "file.txt"), "x", Ct);

        // The directory travels as `git -C <dir>`. The prefix and the relative pathspec both
        // resolve from <dir>, which is only true if git really started there.
        var prefix = await GitCli.RunAsync(sub, ["rev-parse", "--show-prefix"], Ct);
        Assert.True(prefix.Succeeded, prefix.StdErr);
        Assert.Equal("sub/deeper/", prefix.StdOut.Trim());

        var untracked = await GitCli.RunAsync(sub, ["ls-files", "--others", "--", "file.txt"], Ct);
        Assert.True(untracked.Succeeded, untracked.StdErr);
        Assert.Equal("file.txt", untracked.StdOut.Trim());
    }

    [Fact]
    public void CreateStartInfo_PassesTheDirectoryAsArgv_NotWorkingDirectory()
    {
        // CodeQL cs/command-line-injection treats ProcessStartInfo.WorkingDirectory as a command
        // sink, and every project-path route reaches this method (alert #52). `-C` must lead so it
        // applies before git reads any config or path.
        var startInfo = GitCli.CreateStartInfo("-dir-that-looks-like-an-option", ["status"]);
        string[] expected = ["-C", "-dir-that-looks-like-an-option", "-c", "core.fsmonitor=false", "status"];

        Assert.Equal(string.Empty, startInfo.WorkingDirectory);
        Assert.Equal(expected, startInfo.ArgumentList);
    }

    [Fact]
    public async Task RunAsync_MissingDirectory_FailsInsideGit_WithoutThrowing()
    {
        var result = await GitCli.RunAsync(Path.Combine(_root, "missing"), ["status"], Ct);

        // git dies with 128 on `-C` to a missing directory; a failed process start would be -1.
        Assert.Equal(128, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task RunAsync_ForcesFsmonitorOff_EvenWhenTheRepoEnablesIt()
    {
        Assert.True((await GitCli.RunAsync(_root, ["init"], Ct)).Succeeded, "git init");
        Assert.True((await GitCli.RunAsync(_root, ["config", "core.fsmonitor", "true"], Ct)).Succeeded, "enable fsmonitor");

        // Every GitCli child is launched with `-c core.fsmonitor=false`, which outranks the repo
        // config. A one-shot index/object read must never start or hand-shake the fsmonitor daemon,
        // which is what wedged git inside the stdio MCP server (VB-14). `config --get` reports the
        // effective value, so it proves the command-line override actually reached git.
        var result = await GitCli.RunAsync(_root, ["config", "--get", "core.fsmonitor"], Ct);

        Assert.True(result.Succeeded, result.StdErr);
        Assert.Equal("false", result.StdOut.Trim());
    }

    [Fact]
    public async Task RunAsync_ClosesStdin_SoStdinReadingGitGetsEofNotAHang()
    {
        Assert.True((await GitCli.RunAsync(_root, ["init"], Ct)).Succeeded, "git init");

        // hash-object --stdin reads until EOF. GitCli hands git an immediately-closed stdin, so it
        // hashes empty input and exits at once rather than inheriting — and blocking on — the parent's
        // stdin, which for the stdio MCP server is the agent's live JSON-RPC pipe (VB-14).
        var sw = Stopwatch.StartNew();
        var result = await GitCli.RunAsync(_root, ["hash-object", "--stdin"], Ct, timeout: TimeSpan.FromSeconds(5));
        sw.Stop();

        Assert.True(result.Succeeded, result.StdErr);
        Assert.False(result.TimedOut);
        // The well-known SHA-1 of an empty blob: git saw EOF on empty stdin, not an inherited stream.
        Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", result.StdOut.Trim());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(4), $"hash-object hung for {sw.Elapsed}");
    }

    [Fact]
    public void NoSourceLaunchesGitOutsideCreateStartInfo()
    {
        // A git child built by hand skips both guards above: it inherits stdin (the VB-14 hang in
        // the MCP host) and usually sets WorkingDirectory (the CodeQL sink). The code map's file
        // catalog was one until VIBE-170.
        var source = Path.Combine(FindRepositoryRoot(), "VibeRails");
        var separator = Path.DirectorySeparatorChar;
        var offenders = Directory.EnumerateFiles(source, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{separator}bin{separator}")
                && !path.Contains($"{separator}obj{separator}")
                && !path.EndsWith($"{separator}Git{separator}GitCli.cs", StringComparison.Ordinal))
            .Where(path => Regex.IsMatch(
                File.ReadAllText(path),
                """ProcessStartInfo\(\s*"git(\.exe)?"|FileName\s*=\s*"git(\.exe)?"\s*[,}\r\n]"""))
            .Select(path => Path.GetRelativePath(source, path))
            .ToArray();

        Assert.Empty(offenders);
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

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(_root)) return;
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp directory is disposable either way.
        }
    }
}
