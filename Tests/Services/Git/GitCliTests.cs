using System.Diagnostics;
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
