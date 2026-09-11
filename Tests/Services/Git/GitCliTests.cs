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
