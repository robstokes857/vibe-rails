using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VibeRails.Services.LocalFront;

/// <summary>
/// A Windows job object that terminates its processes when its last handle closes, which happens
/// when this process exits for any reason, including Visual Studio's Stop Debugging. Silent
/// breakaway keeps the job to the processes added explicitly: their own children are not in it.
/// </summary>
internal sealed class KillOnCloseJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitSilentBreakawayOk = 0x00001000;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;

    private IntPtr _handle;

    private KillOnCloseJob(IntPtr handle) => _handle = handle;

    /// <summary>Null off Windows, or when the job cannot be created.</summary>
    public static KillOnCloseJob? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle == IntPtr.Zero)
            return null;
        var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitSilentBreakawayOk
            }
        };
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            CloseHandle(handle);
            return null;
        }
        return new KillOnCloseJob(handle);
    }

    /// <summary>Adds a started process. False when Windows refused (it then runs unbound).</summary>
    public bool TryAdd(Process process)
    {
        try
        {
            return _handle != IntPtr.Zero && AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Closes the job, terminating any member process that is still running.</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (handle != IntPtr.Zero)
            CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
