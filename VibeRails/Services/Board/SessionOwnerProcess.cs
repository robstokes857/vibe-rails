using System.Diagnostics;

namespace VibeRails.Services.Board;

/// <summary>
/// Whether the process that recorded a terminal session is still the one running it (VIBE-262).
/// A tab child records its own PID in <c>Sessions.OwnerPid</c> when its session starts, so the
/// owner is still there only while a process with that PID exists and started before the session
/// did: a PID the operating system has since handed to a later process fails the start-time check.
/// A process that cannot be read (gone, or another user's) is not running the session.
/// </summary>
public static class SessionOwnerProcess
{
    // Process start and the recorded StartedUTC come from the same clock; the slack covers rounding.
    private static readonly TimeSpan StartSlack = TimeSpan.FromSeconds(2);

    public static bool IsRunning(int ownerPid, DateTime sessionStartedUtc)
    {
        if (ownerPid <= 0) return false;
        try
        {
            using var process = Process.GetProcessById(ownerPid);
            if (process.HasExited) return false;
            return process.StartTime.ToUniversalTime() <= sessionStartedUtc.ToUniversalTime() + StartSlack;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
