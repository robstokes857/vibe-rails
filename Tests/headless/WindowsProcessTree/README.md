# Disposable Windows process fixture

`WindowsProcessJobTests` builds this helper into `artifacts/windows-process-tree`.
It never initializes VibeRails, opens a database, launches a provider CLI, or contacts a service.
Each process writes its own PID marker in the test's temporary directory and exits within
90 seconds even if a test fails. Tests own kill-on-close jobs as a final cleanup guard.

Run the regressions from the repository root:

```powershell
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~WindowsProcessJobTests|FullyQualifiedName~FirstChanceExceptionPolicyTests"
```

The tests exercise pre-workload tab enrollment, deep/wide/spawning trees, an exited parent,
owner death, repeated/concurrent termination and disposal, invalid enrollment, nested ConPTY
stop without host termination, handle release, and the identity-checked Toolhelp fallback.
Measurements compare three fresh seven-process trees per method. Only the legacy baseline
calls `Process.Kill(true)`; the replacement calls `WindowsProcessJob.TryTerminate`.
The first-chance counter observes the executing thread. Managed allocations include a counting
Serilog sink rendering the legacy warning body; `logBytes` excludes file prefixes and disk I/O.
Measurements exclude launch, output draining and recording finalization and impose no latency
gate. A zero-error snapshot fallback is still best-effort, not a containment guarantee.

Live shell/provider, VS Code and native-terminal verification remains separate from these
disposable tests. Validate the final Windows Native AOT release on a machine with the complete
MSVC and Windows SDK toolchain.
