using System.Diagnostics;
using System.Text;
using VibeRails.Services.Git;
using VibeRails.Utils;
using Xunit;

namespace Tests.Services.Git;

public sealed class GitCommandRunnerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "git-command-" + Guid.NewGuid().ToString("N"));
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public GitCommandRunnerTests() => Directory.CreateDirectory(root);

    [Fact]
    public async Task ClosesGitStdinWhileParentHasPendingRead()
    {
        using var parent = new Process { StartInfo = Fixture("pending-stdin") };
        parent.StartInfo.RedirectStandardInput = true;
        parent.StartInfo.RedirectStandardOutput = true;
        parent.Start();
        try
        {
            await parent.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(15), Ct);
            Assert.Equal(0, parent.ExitCode);
            Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391",
                (await parent.StandardOutput.ReadToEndAsync(Ct)).Trim());
        }
        finally { Kill(parent); }
    }

    [Fact]
    public async Task TimeoutKillsTheWholeProcessTree()
    {
        var result = await GitCommandRunner.RunAsync(() => Fixture("tree"), Ct, TimeSpan.FromSeconds(2));
        Assert.True(result.TimedOut);
        Assert.False(result.OutputLimitExceeded);
        AssertExited("tree");
        AssertExited("tree.child");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OutputLimitKillsProducer(bool binary)
    {
        var result = await GitCommandRunner.RunAsync(() => Fixture("flood"), Ct, TimeSpan.FromSeconds(10),
            maxOutputChars: binary ? null : 123, maximumOutputBytes: binary ? 123 : null,
            binaryOutput: binary, killOnOutputLimit: true);
        Assert.True(result.OutputLimitExceeded);
        Assert.False(result.TimedOut);
        Assert.Equal(123, binary ? result.Bytes.Length : result.StdOut.Length);
        AssertExited("flood");
    }

    [Fact]
    public async Task CallerCancellationKillsTreeAndThrows()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var run = GitCommandRunner.RunAsync(() => Fixture("tree"), cancellation.Token, TimeSpan.FromSeconds(30));
        await WaitForPidAsync("tree.child");
        await cancellation.CancelAsync();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        AssertExited("tree");
        AssertExited("tree.child");
    }

    [Fact]
    public async Task ProcessExitBoundsInheritedOutputPipes()
    {
        var clock = Stopwatch.StartNew();
        var result = await GitCommandRunner.RunAsync(() => Fixture("hold-output"), Ct, TimeSpan.FromSeconds(30));
        Assert.True(result.TimedOut);
        Assert.Equal("ready", result.StdOut);
        Assert.Equal("error-ready", result.StdErr);
        Assert.InRange(clock.Elapsed.TotalSeconds, 1.5, 8);
        AssertExited("hold-output");
        // The parent already exited: its descendant is deliberately orphaned and owns the pipe.
        // No process-tree API can find it from the dead parent. Dispose cleans up this fixture.
    }

    [Fact]
    public async Task DeadlineCoversBlockedStdinWrite()
    {
        var result = await GitCommandRunner.RunAsync(() => Fixture("sleep"), Ct, TimeSpan.FromSeconds(1),
            standardInput: new byte[4 * 1024 * 1024]);
        Assert.True(result.TimedOut);
        AssertExited("sleep");
    }

    [Fact]
    public async Task FailureToStartIsAResult()
    {
        var result = await GitCommandRunner.RunAsync(
            () => new ProcessStartInfo(Path.Combine(root, "missing-executable")), Ct);
        Assert.Equal(-1, result.ExitCode);
        Assert.NotEmpty(result.StdErr);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task TextPreservesUnicodeWhitespaceAndNulls_AndTruncationOnlyDrains()
    {
        var full = await GitCommandRunner.RunAsync(() => Fixture("text"), Ct);
        Assert.Equal(7, full.ExitCode);
        Assert.Equal("é漢🙂\0end\r\n", full.StdOut);
        Assert.Equal("  error\r\n", full.StdErr);
        var prefix = await GitCommandRunner.RunAsync(() => Fixture("text"), Ct, maxOutputChars: 4);
        Assert.Equal(7, prefix.ExitCode);
        Assert.Equal("é漢🙂", prefix.StdOut);
        Assert.Equal("  er", prefix.StdErr);
        Assert.True(prefix.OutputLimitExceeded);
        Assert.False(prefix.TimedOut);
    }

    [Fact]
    public async Task NonAsciiInputRoundTripsWithoutBom()
    {
        var text = "é漢🙂\n";
        var result = await GitCommandRunner.RunTextAsync(root, ["stripspace"], Ct,
            standardInput: Encoding.UTF8.GetBytes(text));
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(text, result.StdOut);
        Assert.False(result.OutputLimitExceeded);
    }

    [Fact]
    public async Task ArgumentBoundariesSurviveQuotesWhitespaceAndMetacharacters()
    {
        const string value = "a \"quoted\" value\\with\\slashes\n&;$(literal)";
        var result = await GitCommandRunner.RunTextAsync(root,
            ["-c", "test.literal=" + value, "config", "--get", "test.literal"], Ct);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(value + "\n", result.StdOut);
    }

    [Fact]
    public async Task BinaryOutputPreservesExactBytesAtTheLimit()
    {
        byte[] bytes = [0, 0xff, 0xfe, 13, 10, 0x80];
        var init = await GitCommandRunner.RunTextAsync(root, ["init"], Ct);
        Assert.Equal(0, init.ExitCode);
        var hash = await GitCommandRunner.RunTextAsync(root, ["hash-object", "-w", "--stdin"], Ct,
            standardInput: bytes);
        var result = await GitCommandRunner.RunBinaryAsync(root, ["cat-file", "blob", hash.StdOut.Trim()], Ct,
            maximumOutputBytes: bytes.Length);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(bytes, result.Bytes);
        Assert.False(result.OutputLimitExceeded);
        var capped = await GitCommandRunner.RunBinaryAsync(root, ["cat-file", "blob", hash.StdOut.Trim()], Ct,
            maximumOutputBytes: bytes.Length - 1);
        Assert.Equal(bytes[..^1], capped.Bytes);
        Assert.True(capped.OutputLimitExceeded);
    }

    [Fact]
    public async Task HardCatalogLimitDoesNotApplyToDiagnostics()
    {
        var result = await GitCommandRunner.RunAsync(() => Fixture("stderr-only"), Ct,
            maxOutputChars: 1, killOnOutputLimit: true);
        Assert.Equal(7, result.ExitCode);
        Assert.Equal("diagnostics", result.StdErr);
        Assert.False(result.OutputLimitExceeded);
    }

    private ProcessStartInfo Fixture(string mode)
    {
        var info = new ProcessStartInfo(PathExecutable.Resolve("dotnet")) { UseShellExecute = false };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "GitProcessFixture", "GitProcessFixture.dll"));
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add(Path.Combine(root, mode));
        return info;
    }

    private async Task WaitForPidAsync(string name)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        while (!File.Exists(Path.Combine(root, name))) await Task.Delay(10, deadline.Token);
    }

    private void AssertExited(string name)
    {
        var pid = int.Parse(File.ReadAllText(Path.Combine(root, name)));
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited, $"Process {pid} is still alive"); }
        catch (ArgumentException) { }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(root))
        {
            if (!int.TryParse(File.ReadAllText(file), out var pid)) continue;
            try { using var process = Process.GetProcessById(pid); Kill(process); }
            catch (ArgumentException) { }
        }
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, recursive: true);
    }
}
