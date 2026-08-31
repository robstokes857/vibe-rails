using VibeRails.Daemon;
using Xunit;

namespace Tests.Daemon;

public sealed class DaemonInstanceGuardTests
{
    [Fact]
    public async Task TryAcquire_RemainsExclusiveAcrossAwaitsAndReleasesFromAnotherThread()
    {
        var testToken = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), $"vbd-guard-{Guid.NewGuid():N}");
        var identity = new CurrentUserIdentity("test-user:guard", root);
        DaemonInstanceGuard? first = null;
        DaemonInstanceGuard? reacquired = null;
        try
        {
            first = DaemonInstanceGuard.TryAcquire("viberails-job-daemon", identity, root);
            Assert.NotNull(first);

            await Task.Yield();
            var blocked = await Task.Run(
                () => DaemonInstanceGuard.TryAcquire("viberails-job-daemon", identity, root),
                testToken);
            Assert.Null(blocked);

            var held = first;
            await Task.Run(held.Dispose, testToken);
            first = null;

            reacquired = await Task.Run(
                () => DaemonInstanceGuard.TryAcquire("viberails-job-daemon", identity, root),
                testToken);
            Assert.NotNull(reacquired);

            if (OperatingSystem.IsWindows())
            {
                Assert.StartsWith(
                    @"Global\viberails-job-daemon-",
                    DaemonInstanceGuard.BuildMutexName("viberails-job-daemon", identity),
                    StringComparison.Ordinal);
            }
            else
            {
                var path = DaemonInstanceGuard.BuildLockFilePath(
                    "viberails-job-daemon",
                    identity,
                    root);
                Assert.True(File.Exists(path));
                Assert.DoesNotContain("Global", path, StringComparison.Ordinal);
            }
        }
        finally
        {
            first?.Dispose();
            reacquired?.Dispose();
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildLockFilePath_IsCanonicalScopedAndContainedInRequestedDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"vbd-lock-path-{Guid.NewGuid():N}");
        var nonCanonicalDirectory = Path.Combine(root, "nested", "..", "locks");
        var identity = new CurrentUserIdentity("uid:4242", root, unixUserId: 4242);

        var path = DaemonInstanceGuard.BuildLockFilePath(
            "VibeRails.JobDaemon",
            identity,
            nonCanonicalDirectory);

        var expectedDirectory = Path.GetFullPath(nonCanonicalDirectory);
        Assert.Equal(
            Path.Combine(expectedDirectory, $".VibeRails.JobDaemon-{identity.ScopeKey}.lock"),
            path);
        Assert.Equal(expectedDirectory, Path.GetDirectoryName(path));
    }
}
