using System.Diagnostics;
using VibeRails.Services.Board;
using Xunit;

namespace Tests.Services.Board;

/// <summary>
/// The cross-window liveness rule (VIBE-262): a session's recorded owner process is still running it
/// only while that PID is alive and the process started before the session did.
/// </summary>
public sealed class SessionOwnerProcessTests
{
    [Fact]
    public void ALiveOwnerThatStartedBeforeTheSession_IsRunningIt()
    {
        using var self = Process.GetCurrentProcess();
        var started = self.StartTime.ToUniversalTime();
        Assert.True(SessionOwnerProcess.IsRunning(Environment.ProcessId, started.AddMinutes(1)));
        // Local and UTC times name the same instant.
        Assert.True(SessionOwnerProcess.IsRunning(Environment.ProcessId, started.AddMinutes(1).ToLocalTime()));
    }

    [Fact]
    public void APidHandedToALaterProcess_IsNotTheOwner()
    {
        using var self = Process.GetCurrentProcess();
        // The session was recorded ten minutes before this process existed: the PID was reused.
        Assert.False(SessionOwnerProcess.IsRunning(Environment.ProcessId, self.StartTime.ToUniversalTime().AddMinutes(-10)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    [InlineData(int.MaxValue)]
    public void NoSuchProcess_IsNotRunningIt(int pid) =>
        Assert.False(SessionOwnerProcess.IsRunning(pid, DateTime.UtcNow));
}
