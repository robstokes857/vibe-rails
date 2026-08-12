# Services/Cli — `ICliWrapper`

The one place that knows how to spin up a process. Injectable, logged, and deliberately
general-purpose: Environment Steps are its first consumer, but nothing about it is step-shaped.

If you are about to write `Process.Start`, `new ProcessStartInfo`, or `Cli.Wrap(...)` anywhere in
this app, use this instead.

## The two methods

| | Use when | You get |
|---|---|---|
| `RunAsync(CliRequest, onLine?, ct)` | You need the output. Hidden window, stdio piped. | Exit code, full stdout/stderr, and an optional per-line callback (stdout and stderr interleaved, tagged via `CliOutputLine.IsError`). |
| `RunInNewTerminalAsync(CliTerminalRequest, ct)` | The **user** needs the output. Its own OS terminal window. | Exit code only. Nothing is captured — they are watching it. |

Both block until the process finishes. Neither throws on a non-zero exit: `CliResult.IsSuccess`
is `ExitCode == 0 && !TimedOut && !Cancelled`, and `DescribeFailure()` renders the rest as a
phrase you can put in a log line or a toast.

Neither throws on a failure to *start*, either. A missing executable, a working directory that
does not exist, or a terminal emulator that is not installed all come back as a failed
`CliResult` with the reason in `StandardError`. Callers render process failures; they should not
also have to catch them.

```csharp
// "Run a thing and read the output" — the step Test button, and anything like it.
var result = await _cli.RunAsync(
    new CliRequest("git", ["status", "--porcelain"], workDir),
    line => { console.WriteLine(line.Text); return ValueTask.CompletedTask; },
    ct);

// "Run a thing the user watches" — Environment Steps.
var result = await _cli.RunInNewTerminalAsync(
    new CliTerminalRequest("npm ci", workDir, StartMinimized: true, Timeout: TimeSpan.FromMinutes(5)),
    ct);
```

## What lives here so callers never re-solve it

- Serilog `[Cli]` logging of the command, its duration, and how it ended.
- Timeout with kill-tree, defaulting to `CliWrapper.DefaultTimeout` (10 minutes).
- Cancellation normalised into `Cancelled` vs `TimedOut` rather than an exception the caller has
  to classify.
- Working-directory validation (expanded, `GetFullPath`'d, must exist).
- Env-var merge, and — for the visible path — env vars baked into the generated script, because
  `UseShellExecute = true` makes `ProcessStartInfo.Environment` unusable.

`RunAsync` is built on CliWrap with `PipeTarget.Merge(ToStringBuilder, ToDelegate)` so a caller
gets both the live lines and the full buffer. `PyBridge/src/PyBridge/PythonRunner.cs` is the
closest sibling if you need a reference.

**Namespace gotcha:** this namespace's last segment is `Cli`, which wins over CliWrap's static
`Cli` class during name resolution. Write `CliWrap.Cli.Wrap(...)` in here.

## `TerminalScriptBuilder`

The visible path and the hidden path build the *same script* from the same builder. That is not
tidiness — it is the only thing making "this step passed its test" mean anything about how the
step will behave at launch. PATH resolution, profile loading, the working-directory guard, and
exit-code capture must not diverge between the two.

Three deliberate differences from `BaseLlmCliLauncher.LaunchInWindowsTerminal`:

1. **No `-NoExit`.** The window closes when the script ends. A *failing* script holds its own
   window open with the exit code on screen.
2. **No `-NoProfile`**, and a login shell on POSIX. Steps run user commands (`npm`, `nvm`,
   `pyenv`) that are frequently only on PATH because of the profile. `CliSpawnCommandBuilder`
   documents the same reasoning for the Job path.
3. **We wait, and we get an exit code.**

### Completion is a three-way race

`race(sentinel file appears, process exits, timeout)`. One rule, every platform:

- **Windows** — `Process.Start` with `UseShellExecute = true` returns the real `pwsh` handle, so
  process-exit and kill-on-timeout both work. The sentinel is the exit-code channel.
- **macOS / Linux** — `osascript` and `gnome-terminal` detach, so there is no useful handle. The
  sentinel is the *only* signal, and the timeout is enforced by giving up on the poll. A window
  the user closed before the sentinel was written is treated as a **failure**, never as an
  unknown success.

The exit-code capture is lifted from `HostShellCommandService.BuildPowerShellCommand`: `$?` is
read for the last statement **first**, because a failed cmdlet never touches `$LASTEXITCODE` and
would otherwise read as success on a stale zero.

## Later migrations

`GitProcessRunner` and `ShellService` are the obvious next consumers. Neither has moved yet.
