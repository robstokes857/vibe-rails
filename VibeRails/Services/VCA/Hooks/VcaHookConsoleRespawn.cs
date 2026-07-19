using System.Runtime.InteropServices;
using System.Text;

namespace VibeRails.Services.VCA.Hooks;

/// <summary>
/// Re-launches the VCA hook host in a child process with its own console window.
/// </summary>
/// <remarks>
/// <para>
/// The previous approach used <c>AllocConsole</c>, which fails when the
/// current process already has a console attached (e.g. when committing from a real
/// terminal or VS Code's integrated terminal). It also can't produce a popup when the
/// hook was launched from a GUI Git client that captured stdout/stderr — the new
/// console window appears but often closes within 800ms on success before the user
/// ever sees it.
/// </para>
/// <para>
/// This class instead re-spawns the current executable with the Win32
/// <c>CREATE_NEW_CONSOLE</c> creation flag. The child process gets a fresh, independent
/// console window in every scenario — terminal, VS Code SCM panel, GUI Git client —
/// and stays open until the user presses Enter or the two-minute timeout elapses (see
/// <see cref="VcaHookProcessHost"/>). The parent process writes a short status line to
/// its own stdout/stderr so the original caller (e.g. VS Code's logs) still sees that
/// the hook ran and what the outcome was.
/// </para>
/// </remarks>
internal static class VcaHookConsoleRespawn
{
    private const uint CREATE_NEW_CONSOLE = 0x00000010;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint STILL_ACTIVE = 259;
    private const uint WAIT_OBJECT_0 = 0;
    private const uint WAIT_FAILED = 0xFFFFFFFF;
    private const uint PollIntervalMs = 500;

    // Once the popup child has been launched we must NOT return null: that makes
    // VcaHookProcessHost fall through and run the entire validation again in-process
    // (a duplicate popup and a duplicate acknowledgment prompt). If we can no longer
    // track the child, fail closed with this code so the commit is blocked and the
    // user can simply retry.
    private const int PostLaunchFailureExitCode = 1;

    /// <summary>
    /// Re-launch the current process in a new console window. Returns <c>null</c> only
    /// when the respawn is skipped <em>before</em> a child is launched (non-Windows, CI,
    /// already the child, or CreateProcess failed) so the caller can fall through to
    /// in-process execution. Once a popup child exists this always returns an exit code —
    /// the child's own code, or a non-zero fail-closed code if the child can no longer be
    /// tracked — so the validation never runs twice.
    /// </summary>
    public static async Task<int?> TryRespawnAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() ||
            !Environment.UserInteractive ||
            IsAutomatedEnvironment())
        {
            return null;
        }

        var hasConsoleWindow = false;
        var hasAttached = false;
        foreach (var arg in args)
        {
            if (arg.Equals("--console-window", StringComparison.OrdinalIgnoreCase))
            {
                hasConsoleWindow = true;
            }
            else if (arg.Equals("--console-window-attached", StringComparison.OrdinalIgnoreCase))
            {
                hasAttached = true;
            }
        }

        if (!hasConsoleWindow || hasAttached)
        {
            return null;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) || !File.Exists(processPath))
        {
            return null;
        }

        // Swap --console-window for --console-window-attached so the child knows it already
        // has its own console and should pause at the end. WithManagedEntry re-passes the
        // entry DLL for framework-dependent (dotnet host) launches so the child runs this app.
        var respawnArgs = VcaHookProcessLaunch.WithManagedEntry(
            processPath,
            args.Select(arg => arg.Equals("--console-window", StringComparison.OrdinalIgnoreCase)
                    ? "--console-window-attached"
                    : arg)
                .ToList());

        var commandLine = BuildCommandLine(processPath, respawnArgs);
        var workingDirectory = Directory.GetCurrentDirectory();

        await output.WriteLineAsync("VibeRails VCA: opening popup console window...");
        await output.FlushAsync();

        var processInfo = CreateChildProcess(commandLine, workingDirectory);
        if (processInfo.hProcess == IntPtr.Zero)
        {
            var win32Error = Marshal.GetLastWin32Error();
            await error.WriteLineAsync(
                $"VibeRails VCA: could not open popup console (Win32 error {win32Error}). " +
                "Running in the current terminal instead.");
            await error.FlushAsync();
            return null;
        }

        try
        {
            while (true)
            {
                var waitResult = WaitForSingleObject(processInfo.hProcess, PollIntervalMs);
                if (waitResult == WAIT_OBJECT_0)
                {
                    break;
                }

                if (waitResult == WAIT_FAILED)
                {
                    var win32Error = Marshal.GetLastWin32Error();
                    _ = TerminateProcess(processInfo.hProcess, (uint)PostLaunchFailureExitCode);
                    await error.WriteLineAsync(
                        $"VibeRails VCA: lost track of the popup console (Win32 error {win32Error}). "
                        + "Commit blocked; re-run the commit or use git commit --no-verify to bypass.");
                    await error.FlushAsync();
                    return PostLaunchFailureExitCode;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    _ = TerminateProcess(processInfo.hProcess, 130);
                    return 130;
                }
            }

            if (!GetExitCodeProcess(processInfo.hProcess, out var exitCode))
            {
                var win32Error = Marshal.GetLastWin32Error();
                await error.WriteLineAsync(
                    $"VibeRails VCA: could not read the popup console result (Win32 error {win32Error}). "
                    + "Commit blocked; re-run the commit or use git commit --no-verify to bypass.");
                await error.FlushAsync();
                return PostLaunchFailureExitCode;
            }

            if (exitCode == STILL_ACTIVE)
            {
                // WaitForSingleObject already reported the process as signaled, so this
                // should be impossible. Fail closed rather than risk a duplicate in-process
                // run by returning null.
                await error.WriteLineAsync(
                    "VibeRails VCA: popup console returned an ambiguous result. "
                    + "Commit blocked; re-run the commit or use git commit --no-verify to bypass.");
                await error.FlushAsync();
                return PostLaunchFailureExitCode;
            }

            await output.WriteLineAsync($"VibeRails VCA: popup console closed (exit code {exitCode}).");
            await output.FlushAsync();

            return (int)exitCode;
        }
        finally
        {
            if (processInfo.hThread != IntPtr.Zero)
            {
                _ = CloseHandle(processInfo.hThread);
            }
            if (processInfo.hProcess != IntPtr.Zero)
            {
                _ = CloseHandle(processInfo.hProcess);
            }
        }
    }

    private static string BuildCommandLine(string executable, IReadOnlyList<string> args)
    {
        var sb = new StringBuilder();
        sb.Append(VcaHookProcessLaunch.QuoteArgument(executable));
        foreach (var arg in args)
        {
            sb.Append(' ');
            sb.Append(VcaHookProcessLaunch.QuoteArgument(arg));
        }
        return sb.ToString();
    }

    private static PROCESS_INFORMATION CreateChildProcess(string commandLine, string workingDirectory)
    {
        var startupInfo = new STARTUPINFO
        {
            cb = Marshal.SizeOf<STARTUPINFO>()
        };
        var processInfo = new PROCESS_INFORMATION();

        var creationFlags = CREATE_NEW_CONSOLE | CREATE_UNICODE_ENVIRONMENT;

        var success = CreateProcess(
            lpApplicationName: null,
            lpCommandLine: commandLine,
            lpProcessAttributes: IntPtr.Zero,
            lpThreadAttributes: IntPtr.Zero,
            bInheritHandles: false,
            dwCreationFlags: creationFlags,
            lpEnvironment: IntPtr.Zero,
            lpCurrentDirectory: workingDirectory,
            lpStartupInfo: ref startupInfo,
            lpProcessInformation: out processInfo);

        return success ? processInfo : new PROCESS_INFORMATION();
    }

    private static bool IsAutomatedEnvironment()
    {
        string[] variables =
        [
            "CI",
            "TF_BUILD",
            "GITHUB_ACTIONS",
            "GITLAB_CI",
            "JENKINS_URL",
            "TEAMCITY_VERSION",
            "BUILDKITE",
            "APPVEYOR"
        ];

        return variables.Any(variable =>
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
