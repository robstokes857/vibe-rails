using VibeRails.Services.Terminal;
using Xunit;

namespace Tests.Services.Terminal;

/// <summary>
/// A remote viewer is unlocked only by the PIN. Clearing the PIN while a session is attached to the
/// relay must lock that session, not open it (VIBE-310).
/// </summary>
public sealed class RemoteViewerLockTests
{
    [Fact]
    public void AViewerStartsLocked() => Assert.Equal(RemoteViewerLock.Locked, RemoteViewerLock.Initial);

    [Theory]
    [InlineData(RemoteViewerLock.Locked, true, RemoteViewerLock.Locked)]
    [InlineData(RemoteViewerLock.Unlocked, true, RemoteViewerLock.Unlocked)]
    // The PIN was cleared mid-session: a locked viewer stays locked and an unlocked one is locked again.
    [InlineData(RemoteViewerLock.Locked, false, RemoteViewerLock.Locked)]
    [InlineData(RemoteViewerLock.Unlocked, false, RemoteViewerLock.Locked)]
    // Any other value is not "unlocked".
    [InlineData(7, true, RemoteViewerLock.Locked)]
    public void OnlyAnUnlockedViewerWithAPinConfiguredStaysUnlocked(int state, bool pinConfigured, int expected) =>
        Assert.Equal(expected, RemoteViewerLock.Current(state, pinConfigured));

    [Fact]
    public void TheRunnerNeverTreatsAMissingPinAsOpen()
    {
        var runner = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "VibeRails", "Services", "Terminal", "Core", "TerminalRunner.cs"));

        // The old fail-open shape: authorized whenever no PIN is configured.
        Assert.DoesNotContain("IsPinConfigured ? 0 : 1", runner, StringComparison.Ordinal);
        Assert.Contains("RemoteViewerLock.Current(", runner, StringComparison.Ordinal);
        // The only place the flag is set to unlocked is the verified-PIN branch.
        var unlocks = runner.Split("Volatile.Write(ref remoteViewerAuthorized, 1)").Length - 1;
        Assert.Equal(1, unlocks);
        var verified = runner.IndexOf("if (verified)", StringComparison.Ordinal);
        var unlock = runner.IndexOf("Volatile.Write(ref remoteViewerAuthorized, 1)", StringComparison.Ordinal);
        Assert.True(verified >= 0 && unlock > verified && unlock - verified < 400, "the unlock must sit inside the verified-PIN branch");
        // A reconnect never pushes a snapshot on its own.
        var reconnect = runner.IndexOf("async Task HandleRemoteReconnectedAsync()", StringComparison.Ordinal);
        var end = runner.IndexOf("[Remote] Failed to handle reconnect", reconnect, StringComparison.Ordinal);
        Assert.True(reconnect >= 0 && end > reconnect);
        Assert.DoesNotContain("PushSnapshotTo", runner[reconnect..end], StringComparison.Ordinal);
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
