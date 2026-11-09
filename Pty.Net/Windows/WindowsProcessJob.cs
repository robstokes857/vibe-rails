using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Pty.Net.Windows;

/// <summary>
/// Owns a non-inheritable, kill-on-close Windows job. Enroll before allowing a process to run
/// workloads. No breakaway flags are permitted; nested PTYs remain contained by their tab job.
/// </summary>
public sealed class WindowsProcessJob : IDisposable
{
    private readonly object gate = new();
    private readonly JobHandle handle;

    /// <summary>Creates an empty job; failure is fatal to launch, never an uncontained launch.</summary>
    public WindowsProcessJob()
    {
        handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Create terminal job failed");
        var limits = new ExtendedLimits { Basic = new BasicLimits { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(handle, 9, ref limits, Marshal.SizeOf<ExtendedLimits>()))
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Set terminal job limits failed");
        }
    }

    /// <summary>Assigns a suspended process. Caller must abort launch if this fails.</summary>
    public void Assign(SafeHandle process)
    {
        lock (gate)
        {
            if (!AssignProcessToJobObject(handle, process))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Enroll terminal process failed");
        }
    }

    /// <summary>Duplicates a temporary non-inheritable enrollment handle into the child.</summary>
    public long DuplicateInto(SafeHandle process)
    {
        lock (gate)
        {
            if (!DuplicateHandle(GetCurrentProcess(), handle, process, out var duplicate, 0, false, 2))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Duplicate terminal job failed");
            return duplicate.ToInt64();
        }
    }

    /// <summary>Terminates the group without inspecting any unrelated process. Returns a native error.</summary>
    public int TryTerminate()
    {
        lock (gate)
        {
            if (handle.IsClosed) return 0; // Closing the last owned handle already terminates the group.
            return TerminateJobObject(handle, 1) ? 0 : Marshal.GetLastWin32Error();
        }
    }

    /// <summary>Closes the sole long-lived handle, including after the original process exits.</summary>
    public void Dispose() { lock (gate) handle.Dispose(); }

    /// <summary>Child-only enrollment, before services or descendants start; closes its temporary handle.</summary>
    public static void JoinBootstrapJob(long value)
    {
        using var job = new JobHandle(new IntPtr(value));
        using var self = new SafeProcessHandle(GetCurrentProcess(), ownsHandle: false);
        if (!AssignProcessToJobObject(job, self))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Enroll terminal host failed");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    private sealed class JobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public JobHandle() : base(true) { }
        public JobHandle(IntPtr value) : base(true) { SetHandle(value); }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern JobHandle CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(JobHandle job, int infoClass, ref ExtendedLimits limits, int size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(JobHandle job, SafeHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(JobHandle job, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, JobHandle source, SafeHandle targetProcess,
        out IntPtr target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
