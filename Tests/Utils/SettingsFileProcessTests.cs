using System.Diagnostics;
using Moq;
using VibeRails.DTOs;
using VibeRails.Routes;
using VibeRails.Services.HttpRelay;
using VibeRails.Services.Integrations.VibeCodeRemote;
using VibeRails.Utils;
using Xunit;

namespace Tests.Utils;

/// <summary>
/// Two separate xUnit processes exercise the product settings component and actual route/key
/// mutation helpers. Only these fixtures choose a disposable file; Config's runtime path is fixed.
/// </summary>
public sealed class SettingsFileProcessTests
{
    private const string FixtureVariable = "VIBERAILS_TEST_SETTINGS_FIXTURE";
    private const string RoleVariable = "VIBERAILS_TEST_SETTINGS_ROLE";
    private static readonly string FixtureRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "settings-coordination-tests"));
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ManualKeyChangeInAnotherProcessPreventsLinkFromOverwritingIt()
    {
        using var fixture = new Fixture();
        using var first = StartWorker(fixture.Directory, "manual-key-holder");
        await WaitForMarkerAsync(fixture.Directory, "holder-ready");
        using var second = StartWorker(fixture.Directory, "link-waiter");

        // The real link TrySave must time out while the other process owns the whole update.
        // This handshake proves actual contention; it does not rely on a scheduling delay.
        await WaitForMarkerAsync(fixture.Directory, "waiter-blocked");
        Mark(fixture.Directory, "release-holder");
        await first.AssertSucceededAsync();
        Mark(fixture.Directory, "resume-waiter");
        await second.AssertSucceededAsync();

        var final = fixture.Store.LoadFresh();
        Assert.Equal("manual-new-key", final.ApiKey);
        Assert.Equal("original-computer", final.ComputerName);
        Assert.Equal(["fixture-stage"], final.TokenSaverStageOverride);
        Assert.Equal("pin-salt-fixture", final.PinSalt);
        Assert.Equal("pin-hash-fixture", final.PinHash);
    }

    [Fact]
    public async Task ComputerNameUpdateInAnotherProcessPreservesTheNewlyLinkedKeyAndOtherSettings()
    {
        using var fixture = new Fixture();
        using var first = StartWorker(fixture.Directory, "link-holder");
        await WaitForMarkerAsync(fixture.Directory, "holder-ready");
        using var second = StartWorker(fixture.Directory, "name-waiter");

        await WaitForMarkerAsync(fixture.Directory, "waiter-blocked");
        Mark(fixture.Directory, "release-holder");
        await first.AssertSucceededAsync();
        Mark(fixture.Directory, "resume-waiter");
        await second.AssertSucceededAsync();

        var final = fixture.Store.LoadFresh();
        Assert.Equal("linked-new-key", final.ApiKey);
        Assert.Equal("updated-computer", final.ComputerName);
        Assert.Equal(["fixture-stage"], final.TokenSaverStageOverride);
        Assert.True(final.UseVsCodeTheme);
        Assert.True(final.CodexLlmProxyEnabled);
        Assert.Equal("pin-hash-fixture", final.PinHash);
    }

    [Fact]
    public async Task TerminatedMutexOwnerDoesNotBlockTheNextProcessOrDamageTheFile()
    {
        using var fixture = new Fixture();
        using var abandoned = StartWorker(fixture.Directory, "abandon-lock");
        await abandoned.AssertSucceededAsync();

        // The abandoned mutex grants ownership to this process. The existing complete JSON
        // remains readable and can be updated without manual lock-file cleanup.
        using (fixture.Store.AcquireWriteLock())
        {
            var settings = fixture.Store.LoadFresh();
            settings.ApiKey = "linked-after-exit";
            fixture.Store.Save(settings);
        }
        var final = fixture.Store.LoadFresh();
        Assert.Equal("linked-after-exit", final.ApiKey);
        Assert.Equal("original-computer", final.ComputerName);
    }

    [Fact]
    public void ExceptionReleasesBothGatesAndNestedWritesAreReentrant()
    {
        using var fixture = new Fixture();
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var lease = fixture.Store.AcquireWriteLock();
            _ = fixture.Store.LoadFresh();
            throw new InvalidOperationException("fixture interruption");
        }));
        // A different thread must be able to acquire both gates after the exception.
        var failure = new List<Exception>();
        var thread = new Thread(() =>
        {
            try
            {
                using var lease = fixture.Store.AcquireWriteLock();
                AppSettingsRoutes.UpdateComputerName(new("after-exception"), fixture.Store);
            }
            catch (Exception ex) { failure.Add(ex); }
        });
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        Assert.Empty(failure);
        Assert.Equal("after-exception", fixture.Store.LoadFresh().ComputerName);
    }

    // The existing executable xUnit assembly is the test-only child host. The explicit marker
    // prevents a normal suite from invoking this helper; no product command or environment
    // override is introduced. Parent tests run this one method using -explicit only -method.
    [Fact(Explicit = true)]
    public void ProcessWorker()
    {
        var directory = Path.GetFullPath(Environment.GetEnvironmentVariable(FixtureVariable)
            ?? throw new InvalidOperationException("Missing test fixture."));
        Assert.StartsWith(FixtureRoot + Path.DirectorySeparatorChar, directory, StringComparison.OrdinalIgnoreCase);
        Assert.True(Guid.TryParseExact(Path.GetFileName(directory), "N", out _));
        var role = Environment.GetEnvironmentVariable(RoleVariable);
        using var store = new SettingsFile(Path.Combine(directory, "settings.json"), TimeSpan.FromMilliseconds(350));
        var relay = new Mock<IRemoteHttpRelayClient>();
        var keyStore = new ApiKeyStore(relay.Object, store);

        if (role == "abandon-lock")
        {
            _ = store.AcquireWriteLock();
            _ = store.LoadFresh();
            Environment.Exit(0); // Intentionally leave ownership to the OS abandonment path.
        }
        else if (role is "manual-key-holder" or "link-holder")
        {
            using var lease = store.AcquireWriteLock();
            Assert.Equal("original-key", store.LoadFresh().ApiKey);
            Mark(directory, "holder-ready");
            WaitForMarker(directory, "release-holder");
            if (role == "manual-key-holder")
            {
                AppSettingsRoutes.UpdateSettings(new AppSettingsDto(false, "manual-new-key", true,
                    true, null, null, null, null, null, null, null, null), store, relay.Object);
            }
            else Assert.True(keyStore.TrySave("linked-new-key", "original-key"));
            relay.Verify(r => r.Reset(), Times.Once);
        }
        else if (role is "link-waiter" or "name-waiter")
        {
            var stopwatch = Stopwatch.StartNew();
            // Read/compare/save is the actual protected operation. It cannot pass the peer's
            // mutex merely because this process's own lock is free.
            Assert.Throws<IOException>(() =>
            {
                if (role == "link-waiter") keyStore.TrySave("linked-new-key", "original-key");
                else AppSettingsRoutes.UpdateComputerName(new("updated-computer"), store);
            });
            Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));
            relay.Verify(r => r.Reset(), Times.Never);
            Mark(directory, "waiter-blocked");
            WaitForMarker(directory, "resume-waiter");
            if (role == "link-waiter")
            {
                Assert.False(keyStore.TrySave("linked-new-key", "original-key"));
                Assert.Equal("manual-new-key", keyStore.Read());
                relay.Verify(r => r.Reset(), Times.Never);
            }
            else AppSettingsRoutes.UpdateComputerName(new("updated-computer"), store);
        }
        else throw new InvalidOperationException("Unknown test role.");
    }

    private static Worker StartWorker(string directory, string role)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        foreach (var argument in new[] { typeof(SettingsFileProcessTests).Assembly.Location, "-explicit", "only",
            "-method", "Tests.Utils.SettingsFileProcessTests.ProcessWorker", "-noLogo", "-noColor", "-reporter", "quiet" })
            start.ArgumentList.Add(argument);
        start.Environment[FixtureVariable] = directory;
        start.Environment[RoleVariable] = role;
        return new Worker(Process.Start(start) ?? throw new InvalidOperationException("Test worker failed to start."));
    }

    private static void Mark(string directory, string name) => File.WriteAllText(Path.Combine(directory, name), "ready");

    private static async Task WaitForMarkerAsync(string directory, string name)
    {
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(directory, name)))
        {
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(20), "Child did not reach " + name);
            await Task.Delay(20, Ct);
        }
    }

    private static void WaitForMarker(string directory, string name)
    {
        // This wait deliberately remains on the mutex-owning thread.
        var deadline = Stopwatch.StartNew();
        while (!File.Exists(Path.Combine(directory, name)))
        {
            Ct.ThrowIfCancellationRequested();
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(20), "Parent did not release " + name);
            Thread.Sleep(20);
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(FixtureRoot, Guid.NewGuid().ToString("N"));
        internal SettingsFile Store { get; }

        internal Fixture()
        {
            Store = new SettingsFile(Path.Combine(Directory, "settings.json"));
            Store.Save(new Settings
            {
                ApiKey = "original-key", ComputerName = "original-computer", UseVsCodeTheme = true,
                CodexLlmProxyEnabled = true, TokenSaverStageOverride = ["fixture-stage"],
                PinSalt = "pin-salt-fixture", PinHash = "pin-hash-fixture"
            });
        }

        public void Dispose()
        {
            Store.Dispose();
            var fullPath = Path.GetFullPath(Directory);
            if (fullPath.StartsWith(FixtureRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && System.IO.Directory.Exists(fullPath)) System.IO.Directory.Delete(fullPath, recursive: true);
        }
    }

    private sealed class Worker(Process process) : IDisposable
    {
        private readonly Task<string> _output = process.StandardOutput.ReadToEndAsync(Ct);
        private readonly Task<string> _error = process.StandardError.ReadToEndAsync(Ct);

        internal async Task AssertSucceededAsync()
        {
            await process.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(20), Ct);
            Assert.True(process.ExitCode == 0, "Settings test worker failed: " + await _output + await _error);
        }

        public void Dispose()
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true); // Only this owned test helper.
            process.WaitForExit(5000);
            process.Dispose();
        }
    }
}
