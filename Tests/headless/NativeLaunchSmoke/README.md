# Native launch smoke test

On Windows with PowerShell 7 installed, build from the repository root:

```powershell
dotnet build Tests/headless/NativeLaunchSmoke/NativeLaunchSmoke.csproj --artifacts-path Tests/obj/native-launch-artifacts
Tests/obj/native-launch-artifacts/bin/NativeLaunchSmoke/debug/NativeLaunchSmoke.exe --native-smoke
```

This opt-in test opens four native terminals: script success/cancellation and
preset success/cancellation. Each executes a fixed output/sleep fixture, checks
the native console, captured output, run outcome and child-process termination.
It uses the production dispatcher, native launcher, native runner, PTY and CLI
wrapper with a disposable launch database and mocked provider preparation and
recording sinks. It never starts an installed agent, root backend or scheduler,
or reads/writes the normal application databases. Provider authentication and
the appearance of the user's actual CLI still require a normal application check.
