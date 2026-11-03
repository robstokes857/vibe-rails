using System.Diagnostics;
using System.Text;
using VibeRails.Services.Git;
using VibeRails.Services.GitPreflight;
using Xunit;

namespace Tests.Services.GitPreflight;

/// <summary>
/// VIBE-269: committed-scope snapshots read every blob through one <c>git cat-file --batch</c>
/// process instead of one process per blob. These cases pin the protocol handling that makes one
/// process safe: exact bytes, a stream that stays in step after a refused object, and recovery
/// when the process dies.
/// </summary>
public sealed class GitBlobBatchReaderTests : IAsyncLifetime
{
    private string _repository = null!;

    public async ValueTask InitializeAsync()
    {
        _repository = Path.Combine(Path.GetTempPath(), $"git_blob_batch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_repository);
        await GitAsync("init");
        await GitAsync("config", "user.email", "tests@viberails.local");
        await GitAsync("config", "user.name", "VibeRails Tests");
        await GitAsync("config", "core.autocrlf", "false");
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_repository))
        {
            foreach (var file in Directory.EnumerateFiles(_repository, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_repository, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task ReadsManyBlobsExactly_ThroughOneProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        // Larger than the reader's 80 KiB buffer, so content arrives across several reads and the
        // remainder goes straight into the destination.
        var large = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 20_000).Select(i => $"line {i}\r\n")));
        byte[][] blobs =
        [
            Encoding.UTF8.GetBytes("class First { }\n"),
            [],
            [0, 10, 13, 10, 255, 0, 10],
            Encoding.UTF8.GetBytes("no trailing newline"),
            large,
        ];
        var ids = new List<string>();
        foreach (var blob in blobs)
            ids.Add(await HashObjectAsync(blob));

        await using var reader = new GitBlobBatchReader(_repository);
        // Twice through, in order, so every read follows a different predecessor on the stream.
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < blobs.Length; i++)
                Assert.Equal(blobs[i], await reader.TryReadBlobAsync(ids[i], blobs[i].Length, ct));
        }

        Assert.Equal(1, reader.ProcessStartCount);
    }

    [Fact]
    public async Task RefusedObjectsLeaveTheStreamInStep()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Encoding.UTF8.GetBytes("public class Kept { }\n");
        var oversized = Encoding.UTF8.GetBytes(new string('x', 200_000));
        await File.WriteAllBytesAsync(Path.Combine(_repository, "kept.cs"), content, ct);
        await File.WriteAllBytesAsync(Path.Combine(_repository, "oversized.txt"), oversized, ct);
        await GitAsync("add", ".");
        await GitAsync("commit", "-m", "objects");
        var keptId = await RevParseAsync("HEAD:kept.cs");
        var oversizedId = await RevParseAsync("HEAD:oversized.txt");
        var treeId = await RevParseAsync("HEAD^{tree}");
        var commitId = await RevParseAsync("HEAD");

        await using var reader = new GitBlobBatchReader(_repository);

        // Over the caller's limit: skipped on the stream, never returned.
        Assert.Null(await reader.TryReadBlobAsync(oversizedId, oversized.Length - 1, ct));
        Assert.Equal(content, await reader.TryReadBlobAsync(keptId, content.Length, ct));
        // Not a blob.
        Assert.Null(await reader.TryReadBlobAsync(treeId, 1024 * 1024, ct));
        Assert.Null(await reader.TryReadBlobAsync(commitId, 1024 * 1024, ct));
        Assert.Equal(content, await reader.TryReadBlobAsync(keptId, content.Length, ct));
        // Missing from the object store.
        Assert.Null(await reader.TryReadBlobAsync(new string('1', 40), 1024, ct));
        Assert.Equal(content, await reader.TryReadBlobAsync(keptId, content.Length, ct));
        Assert.Equal(oversized, await reader.TryReadBlobAsync(oversizedId, oversized.Length, ct));

        Assert.Equal(1, reader.ProcessStartCount);
    }

    [Theory]
    [InlineData("HEAD")]
    [InlineData("HEAD:kept.cs")]
    [InlineData("0123456789abcdef0123456789abcdef0123456\n")]
    [InlineData("0123456789abcdef0123456789abcdef0123456z")]
    [InlineData("")]
    public async Task NeverSendsAnythingButAnObjectId(string request)
    {
        await using var reader = new GitBlobBatchReader(_repository);

        Assert.Null(await reader.TryReadBlobAsync(request, 1024, TestContext.Current.CancellationToken));
        Assert.Equal(0, reader.ProcessStartCount);
    }

    [Fact]
    public async Task StartsAFreshProcessWhenGitDiesBetweenReads()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Encoding.UTF8.GetBytes("survives a dead process\n");
        var id = await HashObjectAsync(content);
        await using var reader = new GitBlobBatchReader(_repository);
        Assert.Equal(content, await reader.TryReadBlobAsync(id, content.Length, ct));

        using (var git = Process.GetProcessById(reader.ProcessIdForTest!.Value))
        {
            git.Kill(entireProcessTree: true);
            await git.WaitForExitAsync(ct);
        }

        Assert.Equal(content, await reader.TryReadBlobAsync(id, content.Length, ct));
        Assert.Equal(2, reader.ProcessStartCount);
    }

    [Fact]
    public async Task CallerCancellationThrows_AndTheNextReadStillWorks()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Encoding.UTF8.GetBytes("after a cancelled read\n");
        var id = await HashObjectAsync(content);
        await using var reader = new GitBlobBatchReader(_repository);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.TryReadBlobAsync(id, content.Length, cancelled.Token));
        Assert.Equal(content, await reader.TryReadBlobAsync(id, content.Length, ct));
    }

    [Fact]
    public async Task DisposeEndsTheProcess()
    {
        var ct = TestContext.Current.CancellationToken;
        var content = Encoding.UTF8.GetBytes("closed on dispose\n");
        var id = await HashObjectAsync(content);
        var reader = new GitBlobBatchReader(_repository);
        Assert.Equal(content, await reader.TryReadBlobAsync(id, content.Length, ct));
        var processId = reader.ProcessIdForTest!.Value;

        await reader.DisposeAsync();

        Assert.Null(reader.ProcessIdForTest);
        Assert.False(IsRunning(processId));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.TryReadBlobAsync(id, content.Length, ct));
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private async Task<string> HashObjectAsync(byte[] content)
    {
        var path = Path.Combine(_repository, $"blob_{Guid.NewGuid():N}.bin");
        await File.WriteAllBytesAsync(path, content, TestContext.Current.CancellationToken);
        var result = await GitCli.RunAsync(
            _repository, ["hash-object", "-w", "--no-filters", path], TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.StdErr);
        return result.StdOut.Trim();
    }

    private async Task<string> RevParseAsync(string revision)
    {
        var result = await GitCli.RunAsync(_repository, ["rev-parse", revision], TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.StdErr);
        return result.StdOut.Trim();
    }

    private async Task GitAsync(params string[] arguments)
    {
        var result = await GitCli.RunAsync(_repository, arguments, TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)} failed: {result.StdErr}");
    }
}
