# Environment / Worker Steps

## Context

Today an Environment (and a Worker, which is the same `Environments` row with `AutomationWorker = 1`) can only
carry static launch configuration: `CustomArgs`, `CustomPrompt`, `WorkspaceMode`. There is no way to say
"before this agent starts, pull and install" or "after this worker finishes, commit and push". The only
existing hook is `PreparedTerminalSession.SetupCommands`, which is system-generated (MCP registration),
`;`-joined so failures are ignored, and not user-editable.

This adds **Steps**: an ordered list of shell commands attached to an environment, each running *before* the
LLM launches or *after* its PTY exits. Steps execute as ordinary OS processes in their own native terminal
windows — deliberately outside the PTY pipeline — one at a time, blocking, and a failed pre-step aborts the
launch.

Underneath it adds the piece that was actually missing: **`ICliWrapper`**, a general-purpose, injectable,
logged wrapper around CliWrap for spinning up processes. Steps are its first consumer; `GitProcessRunner`
and `ShellService` are obvious later migration targets (out of scope here).

### Decisions already made

| | |
|---|---|
| Core runner | New `ICliWrapper` in `VibeRails/Services/Cli/`, CliWrap as a direct `PackageReference` |
| Step surface | Its own native terminal window, **one at a time**, each waited on before the next fires |
| Per-step toggle | Start **minimized** vs start **on screen** (not hidden-vs-windowed) |
| Pre-step failure | **Always abort** the launch — no per-step override |
| Exit code | **Sentinel file**, raced against process exit and a timeout |
| Post-steps | Everywhere, on PTY exit. For a Worker/Job that is "the agent finished"; for a tab it is "the tab closed" — stated plainly in the editor |
| Scope | All three launch paths (browser tab, native launch, Job/Worker run) |

---

## Part 1 — `ICliWrapper` (the reusable core)

New folder `VibeRails/Services/Cli/`. Add `<PackageReference Include="CliWrap" Version="3.10.4" />` to
`VibeRails/VibeRails.csproj` (already on the graph transitively via `PyBridge`, which is explicitly
`IsAotCompatible`/`IsTrimmable` — so `PublishAot=true` is pre-vetted).

```csharp
public interface ICliWrapper
{
    // Hidden, stdio piped. onLine fires per line as it arrives (stdout+stderr interleaved,
    // tagged). Powers the UI Test button and any future "run a thing and read the output".
    Task<CliResult> RunAsync(
        CliRequest request,
        Func<CliOutputLine, ValueTask>? onLine = null,
        CancellationToken cancellationToken = default);

    // Spawns a visible OS terminal window and blocks until it finishes. No captured output —
    // the user watches it. Exit code arrives via the sentinel file.
    Task<CliResult> RunInNewTerminalAsync(
        CliTerminalRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record CliRequest(
    string Executable,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null,
    TimeSpan? Timeout = null,
    string? StandardInput = null);

public sealed record CliTerminalRequest(
    string ScriptBody,          // shell script text; the wrapper writes the temp script
    string WorkingDirectory,
    bool StartMinimized = false,
    TimeSpan? Timeout = null,
    IReadOnlyDictionary<string, string?>? EnvironmentVariables = null);

public readonly record struct CliOutputLine(bool IsError, string Text, TimeSpan Elapsed);

public sealed record CliResult(
    int ExitCode, bool TimedOut, bool Cancelled,
    string StandardOutput, string StandardError, TimeSpan Duration, string CommandLine)
{
    public bool IsSuccess => ExitCode == 0 && !TimedOut && !Cancelled;
}
```

Cross-cutting concerns live here once: Serilog `[Cli]` logging of command + duration + exit code, timeout
with kill-tree, cancellation normalisation, working directory validation, env-var merge. `RunAsync` uses
`Cli.Wrap(...).WithValidation(CommandResultValidation.None)` with `PipeTarget.Merge(ToDelegate, ToStringBuilder)`
so callers get both live lines and the full buffer — the same technique `PyBridge/src/PyBridge/PythonRunner.cs:68-74`
already uses, which is the closest working reference in the repo.

### `RunInNewTerminalAsync` mechanics

Follows `BaseLlmCliLauncher.LaunchInWindowsTerminal` (`VibeRails/Services/LlmClis/Launchers/BaseLlmCliLauncher.cs:125-193`)
with three deliberate differences:

1. **No `-NoExit`.** The window closes when the script ends.
2. **No `-NoProfile`.** Steps run user commands (`npm`, `nvm`, `pyenv`), which are frequently only on PATH
   because of the profile. `CliSpawnCommandBuilder.cs:46-49` already documents this exact reasoning for the
   Job path — match it. Posix uses a login shell for the same reason.
3. **We wait**, and we get an exit code.

The wrapper writes a temp script that ends by writing its exit code to a sentinel file:

```powershell
# Windows .ps1 — exit-code logic lifted verbatim from
# HostShellCommandService.BuildPowerShellCommand (:540-542). $? must be captured for the LAST
# statement FIRST; a failed cmdlet never touches $LASTEXITCODE and would otherwise read as success.
$__ok = $?; $__code = $global:LASTEXITCODE
...
Set-Content -LiteralPath '<sentinel>' -Value $__exit -NoNewline
if ($__exit -ne 0) { Read-Host "`nStep failed with exit code $__exit. Press Enter to close" }
exit $__exit
```

Completion = **race(sentinel file appears, process exits, timeout)**. This one rule works on every platform:

- **Windows** — `Process.Start` with `UseShellExecute = true` returns the real `pwsh` handle, so process-exit
  and kill-on-timeout both work. Sentinel is the exit-code channel.
- **macOS/Linux** — `osascript`/`gnome-terminal` detach (`BaseLlmCliLauncher.cs:212`, `:259`), so there is no
  useful handle. The sentinel is the *only* signal; timeout is enforced by giving up on the poll.

It also means a failed step can hold its window open for reading without stalling us — we already have the
exit code, and we are aborting anyway.

**Files:** `Services/Cli/ICliWrapper.cs`, `CliWrapper.cs`, `CliModels.cs`, `TerminalScriptBuilder.cs`.
Register `AddSingleton<ICliWrapper, CliWrapper>()` in `MapRegisterServices.cs` (near the other singletons,
~line 121).

---

## Part 2 — Database

Raw `Microsoft.Data.Sqlite` + SQL string constants. No EF, no Dapper.

`VibeRails/DB/SqlStrings.cs` — add next to the Environments block (~line 55):

```sql
CREATE TABLE IF NOT EXISTS EnvironmentSteps (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    EnvironmentId INTEGER NOT NULL,
    Phase INTEGER NOT NULL,               -- 0 = before launch, 1 = after the PTY exits
    Position INTEGER NOT NULL,            -- 0-based, unique within (EnvironmentId, Phase)
    Name TEXT NOT NULL DEFAULT '',
    Command TEXT NOT NULL,
    StartMinimized INTEGER NOT NULL DEFAULT 0,
    TimeoutSeconds INTEGER NOT NULL DEFAULT 600,
    Enabled INTEGER NOT NULL DEFAULT 1,
    CreatedUTC TEXT NOT NULL,
    UpdatedUTC TEXT NOT NULL,
    FOREIGN KEY (EnvironmentId) REFERENCES Environments(Id) ON DELETE CASCADE
);
CREATE INDEX IF NOT EXISTS idx_environment_steps_env
    ON EnvironmentSteps(EnvironmentId, Phase, Position);
```

`ON DELETE CASCADE` (not the FK-less orphan pattern `Sandboxes` uses) because a step is *part of* its
environment and owns no filesystem resource — there is no multi-GB directory delete to keep out of the
transaction. `PRAGMA foreign_keys=ON` is already set at `Repository.cs:53-63`.

Register both constants in **`SqlStrings.InitStatements`** (`:510-539`), *not* `MigrationStatements` — a
brand-new `CREATE TABLE IF NOT EXISTS` is correct for both fresh and legacy DBs. `MigrationStatements` is
only for later `ALTER TABLE`s on this table.

**Model:** `VibeRails/DTOs/EnvironmentStep.cs` — plain mutable class matching `Sandbox.cs`, plus
`public enum EnvironmentStepPhase { PreLaunch = 0, PostExit = 1 }`.

**Repository** (`IRepository.cs` + `Repository.cs`, new `#region EnvironmentStep CRUD` after the Sandbox
region at `:576`):

- `Task<List<EnvironmentStep>> GetStepsForEnvironmentAsync(int environmentId, CancellationToken)`
- `Task<Dictionary<int, List<EnvironmentStep>>> GetStepsForEnvironmentsAsync(IReadOnlyList<int> ids, ...)` —
  bulk read for the list endpoint, mirroring how `EnvironmentRoutes.cs:20` already does one bulk sandbox
  lookup instead of N+1
- `Task ReplaceStepsAsync(int environmentId, IReadOnlyList<EnvironmentStep> steps, CancellationToken)` —
  `BEGIN IMMEDIATE`, `DELETE WHERE EnvironmentId = $id`, re-INSERT in array order stamping `Position`.
  This is exactly `JobStore.ReplaceTriggersAsync` (`JobStore.cs:938-999`); copy that shape.

Add a positional `ReadEnvironmentStep(SqliteDataReader)` mapper beside `ReadSandbox` (`Repository.cs:952`).
**Mappers read by column index** — always append to `SELECT` lists, never insert into the middle.

---

## Part 3 — `IEnvironmentStepRunner`

`VibeRails/Services/Environments/Steps/`.

```csharp
public interface IEnvironmentStepRunner
{
    /// Runs every enabled step for (environmentId, phase) in Position order, one at a time,
    /// each blocking until it finishes. Stops at the first non-zero exit.
    Task<StepRunSummary> RunPhaseAsync(
        int environmentId,
        EnvironmentStepPhase phase,
        string workingDirectory,
        Func<StepProgress, ValueTask>? onProgress = null,
        CancellationToken cancellationToken = default);
}

public sealed record StepRunSummary(bool Success, int StepsRun, EnvironmentStep? FailedStep, CliResult? FailedResult);
public sealed record StepProgress(int Index, int Total, string Name, StepProgressKind Kind, int? ExitCode);
```

Thin by design: load steps → filter `Enabled` → order by `Position` → build the script via the shared
`TerminalScriptBuilder` → `cli.RunInNewTerminalAsync(...)` → on non-zero, stop and return. All the process
mechanics live in `ICliWrapper`, so this is unit-testable against a fake `ICliWrapper` with no processes at all.

`workingDirectory` is the **already-workspace-resolved** directory, so a `Persistent`/`PerRun` environment
runs its steps inside the clone, not the project root.

---

## Part 4 — Launch integration

All three launch paths converge on `TerminalRunner.CreateSessionAsync`
(`VibeRails/Services/Terminal/Core/TerminalRunner.cs:49`), so both hooks go in one file.

### Pre-steps

Insert between `PublishSessionStart` (`:106`) and `Terminal.CreateAsync` (`:120`/`:132`). It must be *before*
`CreateAsync` and not after: on the Job path `spawnCliDirectly` (`:113`) makes the PTY process **be** the CLI,
so there is no "PTY exists, LLM hasn't started" window.

```csharp
if (environmentId is int envId)
{
    var pre = await _stepRunner.RunPhaseAsync(envId, EnvironmentStepPhase.PreLaunch, workDir, ..., ct);
    if (!pre.Success)
        throw new EnvironmentStepFailedException(pre);   // caught at :547
}
```

Throwing lands in the existing rollback `catch` at `:547-577`, which already disposes the remote connection
and the terminal and calls `CompleteSessionAsync(sessionId, -1)`. Nothing new to unwind.

`CreateSessionAsync` takes `envName`, not an id — resolve the id once via the environment lookup
`CommandService.PrepareSessionAsync` already performs, or add an `int? environmentId` parameter threaded from
the callers that have it.

### Post-steps

Do **not** use `terminal.Exited` — it is a synchronous `EventHandler<int>` and on the Job path `JobRunner`
finishes and the process exits immediately after, so async work started there would be killed mid-flight.

Instead `CreateSessionAsync` stashes `(environmentId, workDir)` in a `ConcurrentDictionary<string, PostStepContext>`
keyed by sessionId when the env has post-steps, and `TerminalRunner` exposes:

```csharp
public Task RunPostStepsAsync(string sessionId, int exitCode, CancellationToken ct);  // idempotent
```

Awaited from exactly the two places that own a session's end:

- `TerminalRunner.CompleteSessionAsync` (`:587`) — the CLI and Job paths
- `TerminalSessionService.ScheduleExitCleanup` (`TerminalSessionService.cs:689`) — the browser-tab path,
  which already runs its cleanup on a background `Task.Run`

The entry is removed on first run so a double-call can't fire the steps twice.

### Progress and failure reporting

Every step opens a visible window, so the user can already see what is happening. What they cannot see is
*why the tab never started*. On pre-step failure, publish an `AppEvent` (`environment_step_failed`, carrying
step name + exit code) through `IAppEventBus`. `TerminalTabHostService.RelayChildAppEventsAsync` (`:320`)
already relays child events to the parent and on to the browser, so the frontend only needs one new handler
in `terminal-multitab.js` (beside the existing `session_*` handlers at `:2754-2809`) to raise an error toast.

---

## Part 5 — API

Steps ride on the existing environment endpoints (`VibeRails/Routes/EnvironmentRoutes.cs`) rather than getting
their own CRUD surface — they are a child collection, exactly like `Jobs`/`JobTriggers`:

- `GET /api/v1/environments` (`:20`) — include `steps` per env via the bulk read
- `GET /api/v1/environments/{name}` (`:47`) — include `steps`
- `POST` (`:65`) / `PUT` (`:163`) — accept `steps`; **`null` means "leave untouched"**, matching the existing
  nullable-guard convention (`:706-718`) so an editor opened in a reduced context can't silently wipe them
- `DELETE` (`:243`) — nothing to add; the FK cascades. Update the confirm copy to mention the step count.

One new route, in a new `VibeRails/Routes/EnvironmentStepRoutes.cs` registered in `Routes.cs:11` next to
`EnvironmentRoutes`:

- `POST /api/v1/environments/steps/test` → `text/event-stream`

Copy the SSE handler shape verbatim from `HookRoutes.cs:113-203` (`ContentType`, `CacheControl: no-cache, no-store`,
`X-Accel-Buffering: no`, `StartAsync`, `data: {json}\n\n` + `FlushAsync`, `RequestAborted` as the cancel signal,
errors converted into a terminal event rather than a broken stream). It runs the command through
`ICliWrapper.RunAsync` — **hidden and captured**, because the output has to render in the page — using the
same `TerminalScriptBuilder` output as a real run so PATH and profile behaviour match. Takes the command
inline so an unsaved step can be tested before you commit to it.

**DTOs** in `VibeRails/DTOs/ResponseRecords.cs`, and every one must be registered in
`AppJsonSerializerContext` (`:1378`) — `PublishAot=true` means source-gen JSON only, and
`Tests/AppJsonSerializerContextTests.cs` guards this:

```csharp
public record EnvironmentStepDto(int Id, int Phase, int Position, string Name, string Command,
                                 bool StartMinimized, int TimeoutSeconds, bool Enabled);
// Position is implied by array order and never sent by the client.
public record EnvironmentStepRequest(int Phase, string Name, string Command,
                                     bool StartMinimized = false, int TimeoutSeconds = 600, bool Enabled = true);
public record TestEnvironmentStepRequest(string Command, string? WorkingDirectory, int TimeoutSeconds = 600);
public record EnvironmentStepTestEvent(string Type, string? Line, bool IsError,
                                       int? ExitCode, long? DurationMs, string? Message);
```

`EnvironmentResponse` (`:242`) gains `List<EnvironmentStepDto> Steps`; `CreateEnvironmentRequest` (`:217`) and
`UpdateEnvironmentRequest` (`:231`) gain `List<EnvironmentStepRequest>? Steps = null`.

**Validation** (400s, not 500s — follow `TryParseWorkspaceMode` at `:371-381`): command required and
≤ 24 000 chars, reject control characters other than `\r\n\t` (both rules already exist in
`HostShellCommandService.ValidateRequest:164-180`), `Phase` in {0,1}, `TimeoutSeconds` clamped to 1..3600,
max 20 steps per environment.

**Security:** this endpoint executes arbitrary shell commands as the user. It must sit behind the normal
`CookieAuthMiddleware` path — the skip list is frozen at bootstrap/health/OPTIONS per `API_SEC.md` §1 and
**nothing here gets added to it**.

---

## Part 6 — Frontend

`VibeRails/wwwroot/js/modules/environment-controller.js` is one 2 146-line class; `showEnvironmentForm`
(`:478`) already composes CLI settings + workspace + args. It is dense enough that steps should not be inlined.

**Placement:** a `Steps (2 before · 1 after)` summary button in the env form, opening a **nested modal layer**.
Use the `openCustomizationModal` pattern in `llm-picker-controller.js:365-451` — it appends its own
`.llm-picker-modal-layer`, sets `inert` + `aria-hidden` on existing `#modal-container` children, and traps
focus. A second `app.showModal` would destroy the env form underneath it (`app.js:1098` rebuilds
`innerHTML` wholesale).

New module `VibeRails/wwwroot/js/modules/environment-steps.js` exporting `openStepsEditor(app, { steps, onSave })`
so it is testable in isolation, plus a pure `normalizeSteps` / `serializeSteps` pair.

**Editor contents:**

- Two ordered sections, **Before launch** and **After it exits**, each a list of step rows
- Per row: name, command (`<textarea>`, monospace), start-minimized switch, timeout, enabled switch,
  Test button, delete button, drag handle + up/down buttons
- Reordering: copy the hand-rolled HTML5 DnD in `llm-picker-controller.js:474-620` — drag handle only,
  `is-dragging`/`is-drag-target` classes, drop side from `clientY > rect.top + height/2`, **plus** the
  ArrowUp/ArrowDown keyboard handling and explicit move buttons it already implements. The vendored
  `sortable.min.js` is loaded at `index.html:3491` but unused by any first-party module — don't start now.
- The "After it exits" section carries a plain one-liner: *runs when the terminal closes — for a Worker that
  is when the agent finishes; for a tab it is when you close the tab.*
- Test output: reuse `VcaConsole` (`js/modules/vca-console.js:101-205`) — `begin()`, `writeLine()`, `complete()`,
  tone via `data-tone`. It is already `textContent`-only, which is what you want for arbitrary command output.
  Drive it from `streamGitPreflight`'s reader loop (`git-guard-preflight.js:241-278`), which already handles
  authenticated POST + `response.body.getReader()` + `createSseParser` + `AbortController` cancel.

**House rules that will otherwise bite:**

- **No `window.confirm`.** Use `confirmDialog` from `utils.js:676` for step deletion. There is a sweep test
  over every first-party JS file at `Tests/wwwroot/js/jobs-controller.test.mjs:1493-1510`.
- Any capture-phase Escape listener must start with `if (isConfirmDialogOpen()) return;` — asserted as a
  literal string at `jobs-controller.test.mjs:1429`.
- Omit `steps` from the PUT body when the editor was never opened, so the nullable guard leaves them alone.

**CSS** — append to `VibeRails/wwwroot/style.css` (single file, appended over time). Prefix `env-step-*`.
Reuse the tone pattern from `.git-preflight-step` (`:13199-13269`): a local `--step-tone` var switched by
`[data-tone="success|warning|danger"]`, `color-mix(in srgb, var(--step-tone) 14%, transparent)` fills. Always
write `var(--color-bg-elevated, #232323)` with a fallback — an undefined custom property invalidates the whole
declaration, which is the documented cause of the transparent-background bug (`style.css:6-48`).

---

## Part 7 — Tests

| File | Covers |
|---|---|
| `Tests/DB/EnvironmentStepsSqlTests.cs` | New table against `Data Source=:memory:`, insert/select round-trip, cascade delete. Template: `Tests/DB/EnvironmentSqlTests.cs` |
| `Tests/Services/Cli/CliWrapperTests.cs` | `RunAsync` exit codes, per-line callback ordering, timeout kills, cancellation, cwd + env vars. Real short processes (`cmd /c exit 3`-equivalents) |
| `Tests/Services/Environments/EnvironmentStepRunnerTests.cs` | Order, `Enabled` filter, stop-on-first-failure, sequential (never overlapping), progress callbacks — all against a fake `ICliWrapper` |
| `Tests/Services/Cli/TerminalScriptBuilderTests.cs` | Sentinel write, PowerShell `$?`/`$LASTEXITCODE` precedence, quote escaping, posix `cd` guard |
| `Tests/Routes/EnvironmentStepRoutesTests.cs` | Validation 400s, steps round-trip through POST/PUT/GET, `null` steps leaves them untouched. **One `static SharedClient`, not per-test `new HttpClient`** |
| `Tests/AppJsonSerializerContextTests.cs` | Extend for the new DTOs |
| `Tests/wwwroot/js/environment-steps.test.mjs` | `node --test`, run from repo root. Fake `app` with `apiCall` recording; assert on `normalizeSteps`/`serializeSteps` and rendered HTML strings. Template: `settings-controller.test.mjs` |
| `UITests/tests/environment-steps.spec.js` | Extend `jobs-environment.spec.js`'s `installStatefulApi` fake backend; open the env form → Steps editor → add, reorder, save; assert the intercepted payload |

---

## Part 8 — Docs

- `VibeRails/DB/AGENTS.md` — new table section (schema, business rules, indexes, key operations), add
  `EnvironmentSteps` to the ASCII ER diagram (`:695-780`), bump the `*Last checked:*` footer (`:794`)
- `VibeRails/wwwroot/AGENTS.md` — the Steps editor and the nested-layer choice
- `VibeRails/Services/Terminal/AGENTS.md` — the two new hook points in `CreateSessionAsync`
- New `VibeRails/Services/Cli/AGENTS.md` — short: what `ICliWrapper` is for, the two methods, when to reach
  for which. This is the bit meant to get reused, so it should be findable.

---

## Risks

1. **Tab-start HTTP timeout.** `TerminalTabHostService.StartSessionAsync` (`:165`) proxies to the child over
   `_httpClientFactory.CreateClient()` — default `HttpClient.Timeout` is 100 s. A pre-step chain longer than
   that will fail the tab start even though the steps succeed. Fix: set an explicit long timeout on that
   client (or a `CancellationTokenSource` sized from the steps' total budget). **Verify this early** — it is
   the one thing that could force the design open.
2. **Blocking launches.** Everything is sequential and synchronous by request. A step that hangs holds the
   launch until its timeout. Default `TimeoutSeconds = 600` with the race-on-sentinel rule bounds it, but a
   user typing an interactive command into a step will sit there until timeout. The failure path leaves the
   window open with the error, which is the right place to notice.
3. **Posix window mode is weaker.** No process handle, so timeout enforcement is "stop waiting" rather than
   "kill it", and a user who closes the window before the sentinel is written yields no exit code — treat a
   missing sentinel at timeout as a failure and abort, matching the always-abort rule.
4. **Arbitrary command execution stored in the DB.** No worse than what launching an LLM or the MCP
   `run_shell_command` tool already does, and it is behind the same auth — but the test endpoint makes it a
   one-request shell, so the auth path deserves a deliberate look in review.
5. **Profile loading changes behaviour vs. the Test button** if they diverge. Both must go through
   `TerminalScriptBuilder` so a step that passes its test behaves the same at launch.

---

## Verification

1. `dotnet build` — then `dotnet publish -r win-x64` at least once, to confirm CliWrap survives NativeAOT
   now that it is actually rooted (it has never been exercised under this app's AOT publish).
2. `dotnet test Tests` — full suite, plus the new files above.
3. `node --test Tests/wwwroot/js/environment-steps.test.mjs` from the repo root.
4. `cd UITests && npx playwright test tests/environment-steps.spec.js` — plain `npx playwright test`, not the
   `VIBERAILS_E2E_BACKEND_DLL` isolated build, which 400s on tab creation.
5. Live smoke, in one PowerShell command so `Set-Location` sticks; use an isolated `-o` build directory if
   the dev instance is holding `bin`:
   - `vb.exe --vs-code` → open the OTP link
   - Create an environment with two pre-steps (`echo hello`, `git status`) and one post-step
   - Test each from the editor; confirm live output in the console pane and the right exit code
   - Launch it in a **browser tab** — two windows should appear one at a time, second waiting on the first,
     then the LLM starts
   - Launch it from the **Environments page** (native terminal) — same
   - Set a pre-step to `exit 1` → the window stays open showing the failure, the launch aborts, and an error
     toast names the step
   - Set one step to start minimized → confirm it does not steal focus
   - Close the tab → the post-step window appears
   - Attach the env to an Automation and run it → post-step fires when the agent finishes, before the run
     is marked complete
6. `sqlite3 ~/.vibe_rails/state.db ".schema EnvironmentSteps"` on an **existing** DB to confirm the table is
   created on an already-migrated database, not just a fresh one.
