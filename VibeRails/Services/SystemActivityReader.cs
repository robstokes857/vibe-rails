using System.Globalization;
using System.Runtime.InteropServices;

namespace VibeRails.Services;

/// <summary>Cumulative machine-wide CPU counters: busy time and total time, in platform ticks.</summary>
internal readonly record struct SystemCpuTimes(ulong Busy, ulong Total);

/// <summary>
/// Machine-wide activity signals for <see cref="SystemResourceService"/> (VIBE-172): every process's CPU,
/// not just this one's, and on Windows the time since the user's last keyboard or mouse input. Every read
/// returns null where the platform has no such figure, so callers fall back instead of failing.
/// </summary>
internal static class SystemActivityReader
{
    /// <summary>Cumulative CPU counters for the whole machine, or null when unavailable (macOS, restricted /proc).</summary>
    public static SystemCpuTimes? ReadCpuTimes()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Kernel time includes idle time, so kernel + user is the total.
                if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
                var total = (ulong)kernel + (ulong)user;
                return new(total - Math.Min(total, (ulong)idle), total);
            }
            if (OperatingSystem.IsLinux())
                return ParseProcStat(File.ReadLines("/proc/stat").FirstOrDefault());
        }
        catch (Exception)
        {
            // No system-wide figure on this machine (restricted /proc, missing export): the service
            // falls back to process CPU. The poll loop must never throw: a faulted BackgroundService
            // stops the host.
        }
        return null;
    }

    /// <summary>
    /// Parses the aggregate <c>cpu</c> line of /proc/stat: user nice system idle iowait irq softirq steal
    /// [guest guest_nice]. Guest time is already counted in user and nice, so it is left out; idle and
    /// iowait are not busy.
    /// </summary>
    internal static SystemCpuTimes? ParseProcStat(string? line)
    {
        if (line is null) return null;
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 5 || fields[0] != "cpu") return null;
        ulong total = 0, idle = 0;
        for (var i = 1; i < Math.Min(fields.Length, 9); i++)
        {
            if (!ulong.TryParse(fields[i], NumberStyles.None, CultureInfo.InvariantCulture, out var value)) return null;
            total += value;
            if (i is 4 or 5) idle += value;
        }
        return new(total - idle, total);
    }

    /// <summary>Busy share of the machine between two counter readings (0–100), or null without a usable interval.</summary>
    internal static double? CpuPercent(SystemCpuTimes? previous, SystemCpuTimes? current)
    {
        if (previous is not { } before || current is not { } after) return null;
        // Counters only grow; anything else (a wrap, a different source) is not a usable interval.
        if (after.Total <= before.Total || after.Busy < before.Busy) return null;
        return Math.Clamp((double)(after.Busy - before.Busy) / (after.Total - before.Total) * 100, 0, 100);
    }

    /// <summary>Time since the last keyboard or mouse input in this desktop session (Windows only).</summary>
    public static TimeSpan? ReadUserIdle()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return null;
            // Both are GetTickCount values, which wrap every 49.7 days; unsigned subtraction handles the wrap.
            return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
        }
        catch (Exception)
        {
            return null; // No desktop session API (for example Server Core); never fault the poll loop.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size;
        public uint Time;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo info);
}
