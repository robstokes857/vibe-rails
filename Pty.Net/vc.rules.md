# VibeRails Rules

Rules VibeRails enforces on every commit. Each rule is a list item under the rules heading
below; the optional Files section lists files covered by this policy.

## Vibe Rails Rules
- Log all file changes (STOP)
- Package file changes (STOP)

## Files
- [Windows/WindowsProcessJob.cs](Windows/WindowsProcessJob.cs): non-inheritable job lifetime, pre-workload enrollment, native termination and temporary bootstrap handle transfer (VIBE-302).
- [Windows/WindowsJobBootstrap.cs](Windows/WindowsJobBootstrap.cs): fail-closed tab enrollment barrier before services start (VIBE-302).
- [Windows/WindowsProcessCleanup.cs](Windows/WindowsProcessCleanup.cs): bounded Toolhelp fallback with pinned process identities and native error results (VIBE-302).
- [Windows/NativeMethods.cs](Windows/NativeMethods.cs): resume the suspended enrolled PTY and release native attribute-list resources (VIBE-302); the pseudoconsole handle remembers which console host API created it and closes through that one (VIBE-354).
- [Windows/PseudoConsoleConnection.cs](Windows/PseudoConsoleConnection.cs): own and terminate the PTY job through stop, completion and disposal (VIBE-302); report the console host in use and why a requested bundled host was not used, and resize through the API that created the console (VIBE-354).
- [Mac/PtyConnection.cs](Mac/PtyConnection.cs): hang up the spawned process directly instead of using Darwin's variadic signal ioctl, and tolerate an already-exited child during disposal.
- [Windows/WindowsArguments.cs](Windows/WindowsArguments.cs): quote each argument so CommandLineToArgvW returns it unchanged (backslashes before quotes, empty arguments kept), matching VcaHookProcessLaunch.QuoteArgument (VIBE-194).
- [Windows/PtyProvider.cs](Windows/PtyProvider.cs): resolve programs before creating pipes (VIBE-194); create suspended, enroll in a job, arm the exit watcher, then resume; release resources on failed launches (VIBE-302); create the pseudoconsole through a bundled conpty.dll when its path is given, and fall back to kernel32 with a recorded reason when that cannot be used (VIBE-354).
- [Linux/NativeMethods.cs](Linux/NativeMethods.cs): document that the child environment can only be extended, never reduced (VIBE-194).
- [Properties/AssemblyInfo.cs](Properties/AssemblyInfo.cs): expose internals to the Tests assembly for the quoting and PATH lookup regressions (VIBE-194).
- [PtyConsoleHost.cs](PtyConsoleHost.cs): names the Windows console host a PTY runs under: none, built-in or bundled (VIBE-354).
- [PtyOptions.cs](PtyOptions.cs): optional absolute path of a bundled conpty.dll; unset keeps the built-in host, and the path is never searched for (VIBE-354).
- [IPtyConnection.cs](IPtyConnection.cs): expose the console host and the fallback reason, with defaults so other platforms and test fakes are unaffected (VIBE-354).
- [Windows/ConPtyApi.cs](Windows/ConPtyApi.cs): the two pseudoconsole APIs behind one type: kernel32 by P/Invoke, and conpty.dll loaded once by absolute path with OpenConsole.exe required beside it; every failure is a reason, never an exception (VIBE-354).
