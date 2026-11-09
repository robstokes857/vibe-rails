using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pty.Net.Windows;

/// <summary>Bounded best-effort fallback for an owned process whose job could not be terminated.</summary>
public static class WindowsProcessCleanup
{
    /// <summary>Native failures and bounds are reported; a snapshot cannot certify containment.</summary>
    public readonly record struct Result(int NativeErrors, int LastError, int Snapshots, bool LimitReached);

    /// <summary>
    /// Pins every selected process until completion. Opens only descendants selected by Toolhelp,
    /// validates creation/exit times against the pinned parent, and never throws for inspection races.
    /// Three snapshots and 4096 selected processes bound work even under continuous spawning.
    /// The caller must report that cleanup was best-effort, even if no native call failed.
    /// </summary>
    public static Result TerminateTree(SafeHandle root, int rootPid)
    {
        const int maxProcesses = 4096;
        var known = new Dictionary<int, (SafeHandle Handle, long Created)>();
        var owned = new List<SafeProcessHandle>();
        var rejected = new HashSet<int>();
        int errors = 0, lastError = 0, snapshots = 0;
        bool limit = false;
        void Error(int code) { errors++; lastError = code; }
        if (rootPid == Environment.ProcessId || !GetProcessTimes(root, out var created, out _, out _, out _))
            return new Result(1, rootPid == Environment.ProcessId ? 87 : Marshal.GetLastWin32Error(), 0, false);
        known.Add(rootPid, (root, created));
        try
        {
            for (int pass = 0; pass < 3; pass++)
            {
                using var snapshot = CreateToolhelp32Snapshot(2, 0);
                snapshots++;
                if (snapshot.IsInvalid) { Error(Marshal.GetLastWin32Error()); break; }
                var entries = new List<(int Pid, int Parent)>();
                var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
                if (!Process32FirstW(snapshot, ref entry)) { Error(Marshal.GetLastWin32Error()); break; }
                do
                {
                    entries.Add(((int)entry.Pid, (int)entry.ParentPid));
                    if (entries.Count == 65536) { limit = true; break; }
                } while (Process32NextW(snapshot, ref entry));
                int endError = Marshal.GetLastWin32Error();
                if (!limit && endError != 18) Error(endError); // ERROR_NO_MORE_FILES

                bool added;
                do
                {
                    added = false;
                    foreach (var (pid, parentPid) in entries)
                    {
                        if (known.ContainsKey(pid) || rejected.Contains(pid) || !known.TryGetValue(parentPid, out var parent)) continue;
                        if (known.Count >= maxProcesses) { limit = true; break; }
                        rejected.Add(pid); // Do not repeatedly probe denied or invalid candidates.
                        if (pid == Environment.ProcessId) { Error(87); continue; }
                        var candidate = OpenProcess(0x1001, false, pid); // QUERY_LIMITED_INFORMATION | TERMINATE
                        if (candidate.IsInvalid)
                        {
                            int error = Marshal.GetLastWin32Error();
                            candidate.Dispose();
                            if (error != 87) Error(error); // exited between snapshot and open
                            continue;
                        }
                        if (!GetProcessTimes(candidate, out long childCreated, out _, out _, out _) ||
                            !GetProcessTimes(parent.Handle, out _, out long parentExited, out _, out _))
                        {
                            Error(Marshal.GetLastWin32Error()); candidate.Dispose(); continue;
                        }
                        if (!IsChildLifetime(parent.Created, parentExited, childCreated)) { candidate.Dispose(); continue; }
                        known.Add(pid, (candidate, childCreated));
                        owned.Add(candidate);
                        added = true;
                    }
                } while (added && !limit);

                // Stop parents too, so new children cease. Later snapshots find any child which
                // crossed this snapshot/termination boundary; all parent identities remain pinned.
                foreach (var item in known.Values)
                {
                    if (!GetExitCodeProcess(item.Handle, out uint code)) { Error(Marshal.GetLastWin32Error()); continue; }
                    if (code != 259) continue;
                    if (!TerminateProcess(item.Handle, 1))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (!GetExitCodeProcess(item.Handle, out code) || code == 259) Error(error);
                    }
                }
                if (limit) break;
            }
        }
        finally { foreach (var handle in owned) handle.Dispose(); }
        return new Result(errors, lastError, snapshots, limit);
    }

    internal static bool IsChildLifetime(long parentCreated, long parentExited, long childCreated) =>
        childCreated >= parentCreated && (parentExited == 0 || childCreated <= parentExited);

    /// <summary>Terminates one pinned process; for a failed launch that never ran a workload.</summary>
    public static int TerminateUnstarted(SafeHandle process) =>
        TerminateProcess(process, 1) ? 0 : Marshal.GetLastWin32Error();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public UIntPtr Heap;
        public uint Module, Threads, ParentPid;
        public int Priority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Name;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(SafeHandle process, uint exitCode);
}
