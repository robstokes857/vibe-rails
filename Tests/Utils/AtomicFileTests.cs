using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

public sealed class AtomicFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"viberails-atomic-{Guid.NewGuid():N}");

    public AtomicFileTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ReplacesTheFileWholeAndLeavesNoTemporaryBehind()
    {
        var path = Path.Combine(_root, "settings.json");
        AtomicFile.WriteAllText(path, "{\"first\":true}");
        AtomicFile.WriteAllText(path, "{\"second\":\"é\"}");

        Assert.Equal("{\"second\":\"é\"}", File.ReadAllText(path));
        Assert.NotEqual(0xEF, File.ReadAllBytes(path)[0]);
        Assert.Equal(["settings.json"], Directory.GetFiles(_root).Select(Path.GetFileName));
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void AConcurrentReaderSeesTheOldOrTheNewContentsNeverATornFile()
    {
        var path = Path.Combine(_root, "settings.json");
        var small = "{\"v\":\"" + new string('a', 16) + "\"}";
        var large = "{\"v\":\"" + new string('b', 512 * 1024) + "\"}";
        AtomicFile.WriteAllText(path, small);
        using var stop = new CancellationTokenSource();
        var torn = 0;
        var reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    // The reader Config uses: it shares delete access, so the rename never waits on it.
                    var text = AtomicFile.ReadAllText(path);
                    if (text != small && text != large) Interlocked.Increment(ref torn);
                }
                catch (IOException)
                {
                    // Windows: the rename can hold the name for an instant; that is not a torn read.
                }
                catch (UnauthorizedAccessException)
                {
                    // Windows: opening a file that is mid-replace can be refused; still not a torn read.
                }
                // Real readers (a process starting, a launch re-reading settings) come and go; a
                // loop with no gap at all would keep the old name taken on Windows for good.
                Thread.Sleep(1);
            }
        }, TestContext.Current.CancellationToken);
        for (var i = 0; i < 40; i++)
            AtomicFile.WriteAllText(path, i % 2 == 0 ? large : small);
        stop.Cancel();
        reader.Wait(TestContext.Current.CancellationToken);
        Assert.Equal(0, torn);
    }

    [Fact]
    public void WaitsOutABriefReaderOnWindowsInsteadOfFailingTheSave()
    {
        if (!OperatingSystem.IsWindows()) return;
        var path = Path.Combine(_root, "settings.json");
        AtomicFile.WriteAllText(path, "old");
        var open = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () =>
        {
            await Task.Delay(60, TestContext.Current.CancellationToken);
            await open.DisposeAsync();
        }, TestContext.Current.CancellationToken);
        AtomicFile.WriteAllText(path, "new");
        release.Wait(TestContext.Current.CancellationToken);
        Assert.Equal("new", File.ReadAllText(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }
}
