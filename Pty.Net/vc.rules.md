# VibeRails Rules

Rules VibeRails enforces on every commit. Each rule is a list item under the rules heading
below; the optional Files section lists files covered by this policy.

## Vibe Rails Rules
- Log all file changes (STOP)
- Package file changes (STOP)

## Files
- [Mac/PtyConnection.cs](Mac/PtyConnection.cs): hang up the spawned process directly instead of using Darwin's variadic signal ioctl, and tolerate an already-exited child during disposal.
- [Windows/WindowsArguments.cs](Windows/WindowsArguments.cs): quote each argument so CommandLineToArgvW returns it unchanged (backslashes before quotes, empty arguments kept), matching VcaHookProcessLaunch.QuoteArgument (VIBE-194).
- [Windows/PtyProvider.cs](Windows/PtyProvider.cs): resolve a bare program name only on fully qualified PATH entries and throw when it is missing, instead of falling back to the working directory; resolve before creating pipes (VIBE-194).
- [Linux/NativeMethods.cs](Linux/NativeMethods.cs): document that the child environment can only be extended, never reduced (VIBE-194).
- [Properties/AssemblyInfo.cs](Properties/AssemblyInfo.cs): expose internals to the Tests assembly for the quoting and PATH lookup regressions (VIBE-194).
