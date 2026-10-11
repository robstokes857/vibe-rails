using System.Diagnostics;
using VibeRails.Services;
using VibeRails.Services.Git;
using Xunit;

namespace Tests.Services.Git;

/// <summary>
/// <see cref="GitProcessRunner"/> serves GitService, SandboxService, Jobs, workspaces and hook
/// installation. It must honor the same launch contract as <see cref="GitCli"/>: an inherited stdin
/// pipe with a pending parent read hangs git at startup inside the MCP host (VB-K5SCD-210), and an
/// fsmonitor handshake can hold the output pipes past exit (VB-14).
/// </summary>
public sealed class GitProcessRunnerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-gitrunner-{Guid.NewGuid():N}");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public GitProcessRunnerTests()
    {
        Directory.CreateDirectory(_root);
        Assert.True((GitCli.RunAsync(_root, ["init"], CancellationToken.None).GetAwaiter().GetResult()).Succeeded, "git init");
    }

    [Fact]
    public async Task RunAsync_ForcesFsmonitorOff_EvenWhenTheRepoEnablesIt()
    {
        Assert.True((await GitCli.RunAsync(_root, ["config", "core.fsmonitor", "true"], Ct)).Succeeded, "enable fsmonitor");

        var result = await GitProcessRunner.RunAsync(["config", "--get", "core.fsmonitor"], _root, Timeout, Ct);

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("false", result.StdOut);
    }

    [Fact]
    public async Task RunAsync_ClosesStdin_SoAStdinReadingCommandGetsEof()
    {
        // hash-object --stdin reads until EOF. The runner never passes input, so the only way this
        // returns promptly is a redirected stdin that was closed right after start.
        var sw = Stopwatch.StartNew();
        var result = await GitProcessRunner.RunAsync(["hash-object", "--stdin"], _root, Timeout, Ct);
        sw.Stop();

        Assert.False(result.TimedOut);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391", result.StdOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), $"hash-object hung for {sw.Elapsed}");
    }

    [Fact]
    public async Task RunRawAsync_KeepsOutputUntrimmed_AndReadsPathsAsUtf8()
    {
        var name = "ünï cödé.txt";
        await File.WriteAllTextAsync(Path.Combine(_root, name), "x", Ct);
        Assert.True((await GitCli.RunAsync(_root, ["add", "--", name], Ct)).Succeeded, "git add");

        var result = await GitProcessRunner.RunRawAsync(["ls-files", "--cached", "-z"], _root, Timeout, Ct);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(name + "\0", result.StdOut);
    }

    [Fact]
    public async Task RunAsync_ReportsAFailedStart_InsteadOfThrowing()
    {
        var result = await GitProcessRunner.RunAsync(["rev-parse", "HEAD"], Path.Combine(_root, "does-not-exist"), Timeout, Ct);

        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.NotEmpty(result.StdErr);
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
        catch
        {
            // Best effort; a locked pack file must not fail the test run.
        }
    }
}
