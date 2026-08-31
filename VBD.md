# VibeRails Demon (VBD)

## Status

- **Document type:** Implementation plan
- **Feature name:** VibeRails Demon
- **Short name:** VBD
- **Primary goal:** Allow VibeRails Automations to run while the main VibeRails dashboard/backend is closed.
- **Decision:** Proceed by combining NSWS's per-user background-host lifecycle with VibeRails' existing SQLite-backed Automation scheduler.

"Demon" is the product name. In code and operating-system documentation, the conventional term "daemon" may still appear for the background process.

## Executive Summary

VBD is feasible without replacing the existing Automation system.

VibeRails already has the important job correctness mechanisms:

- Dynamic interval, daily, weekly, pre-commit, post-commit, manual, and retry triggers
- Durable job definitions and run records in `state.db`
- A single-writer SQLite scheduler lease
- Atomic launch and run claims
- Per-Automation overlap protection
- A machine-wide three-terminal launch limit
- Stale launch and dead-process recovery
- Existing Environment, workspace, terminal, timeout, cancellation, session-recording, and run-history behavior

The missing capability is a lightweight process that remains available when the dashboard is closed. VBD will provide that process.

NSWS should be reused for its per-user service lifecycle, single-instance protection, local IPC, and cross-platform process management. Its compiled-in cron registry should **not** become the source of truth for VibeRails Automations, because VibeRails jobs are dynamic database records and can change without rebuilding or restarting the application.

## User Promise and Limits

VBD will support this scenario:

1. The user enables background Automation execution.
2. The operating system starts VBD for that user.
3. The main VibeRails dashboard can be closed.
4. VBD detects due or queued work in `state.db`.
5. VBD launches the same recorded Automation terminal process VibeRails launches today.
6. The next time the dashboard opens, the existing run history and terminal recording are available.

VBD will **not** promise execution when:

- The computer is powered off or asleep
- The user is logged out and no interactive desktop exists
- The configured LLM CLI, project directory, credentials, or required runtime dependencies are unavailable

Current Automations deliberately open visible native terminal sessions. VBD is therefore a per-user interactive background host, not a privileged machine-wide service. A later headless-worker feature would be a separate project.

When the machine resumes after sleep, the existing VibeRails schedule behavior should remain authoritative: enqueue one overdue run for each due trigger, then advance that trigger to its next future occurrence. VBD should not replay every interval missed during a long outage.

## Architecture

```text
User login
   |
   v
OS starts: vb --job-daemon
   |
   v
VibeRails Demon (lean Generic Host)
   |-- no Kestrel
   |-- no browser
   |-- no MCP server
   |-- no BERT/model loading
   |-- no dashboard maintenance jobs
   |
   v
Existing JobSchedulerHostedService
   |
   |-- acquire/renew state.db scheduler lease
   |-- reap dead runs
   |-- fail stalled launches
   |-- enqueue due schedules
   |-- drain durable queued runs
   v
Existing JobLaunchService
   |
   v
vb --env <environment> --job-run <run-id>
   |
   v
Existing CLI, workspace, session recording, timeout,
cancellation, run completion, and terminal history flow
```

When the dashboard is open, its existing scheduler may run at the same time as VBD. The `JobSchedulerLease` row ensures only one process drives the queue. Row-level launch and run claims remain the final duplicate-execution barriers.

## Core Design Decisions

### 1. Keep VibeRails as the scheduler of record

Do not translate each Automation into a CronHost registration. Doing so would duplicate schedule state, enabled state, run state, cancellation, history, and overlap handling. It would also require a daemon restart whenever the user edits an Automation.

The following existing components remain authoritative:

- `VibeRails/DB/JobStore.cs`
- `VibeRails/Services/Jobs/JobSchedulerHostedService.cs`
- `VibeRails/Services/Jobs/JobLaunchService.cs`
- `VibeRails/Services/Jobs/JobRunner.cs`
- `VibeRails/Services/Jobs/JobRunReaper.cs`
- `VibeRails/Services/Jobs/JobScheduleCalculator.cs`

### 2. Reuse NSWS as lifecycle infrastructure

Reuse or generalize these NSWS concepts:

- Current-user install, uninstall, start, stop, restart, and status operations
- Stable absolute executable and argument registration
- A current-user, single-instance guard
- A current-user-only named-pipe control channel
- Graceful shutdown and reachability checks
- `systemd --user` integration
- macOS LaunchAgent integration
- Bounded local status/log protocol patterns

Do not add a build dependency on `C:\source\NSWS` or commit only its existing local `.nupkg`. The final dependency must be reproducible. The recommended approach is to bring the small reusable lifecycle layer into the VibeRails repository as an in-tree project, following the existing `Pty.Net` and `PyBridge` precedent.

Before importing or publishing NSWS code:

- Add an explicit MIT license compatible with VibeRails
- Add package/repository metadata if it remains a package
- Add normal unit tests in addition to the current self-test executable
- Verify Native AOT compatibility in the VibeRails release matrix

### 3. Add a lean process role

Add an internal process mode:

```text
vb --job-daemon
```

`Program.cs` must recognize this mode before creating the ASP.NET web application, just as lightweight hook/process-host modes exit before Kestrel today.

The VBD process will use a small `HostApplicationBuilder` and register only the services needed to:

- Open `state.db`
- Run `JobSchedulerHostedService`
- Resolve Environments
- Resolve or provision run workspaces
- Launch recorded Automation terminals
- Log VBD health
- Serve the local VBD control pipe

It must not call the full web-host registration path or accidentally start global maintenance jobs.

### 4. Share Automation runtime registration

Factor the minimum Automation launch graph out of `MapRegisterServices.Register` into a reusable registration method. Both the dashboard host and VBD must resolve the same implementations for:

- `IJobStore`
- `IRepository`
- `IJobLaunchService`
- `IEnvironmentLaunchService`
- `ILaunchLLMService`
- `IRunWorkspaceService`
- `ISandboxService`
- LLM-specific launchers
- `JobSchedulerHostedService`

This avoids rebuilding the old `--job-tick` problem where a transient host manually constructed a smaller launch graph that could drift from the real Environment launch pipeline.

### 5. Keep the current database lease and claims

VBD must use the existing `JobSchedulerLease`; it must not add a second ownership mechanism for queue processing.

Expected behavior:

- If VBD owns the lease, an open dashboard observes but does not drain the queue.
- If the dashboard owns the lease, VBD remains available and keeps contending normally.
- A graceful owner releases the lease during shutdown.
- After an ungraceful crash, another host takes over after the existing lease expiry.
- `TryMarkLaunchedAsync` and `StartRunAsync` continue protecting the final handoff.

### 6. Add a small current-user IPC contract

VBD needs a control protocol with a narrow command surface:

- `PING` - confirm reachability
- `STATUS` - return version, PID, uptime, last cycle, lease state, and last error
- `KICK` - wake the scheduler after new durable work is queued
- `SHUTDOWN` - request a graceful stop

Do not expose arbitrary command execution over this pipe.

The pipe must:

- Be accessible only to the current user
- Use bounded request and response sizes
- Apply connection and request timeouts
- Include a protocol version
- Treat version mismatch as `NeedsRepair`, not as a silent success

`JobService.RunNowAsync` and `RetryRunAsync` should continue kicking the local scheduler and should also send a best-effort VBD `KICK`. Post-commit enqueue mode may do the same. Durable SQLite rows remain the source of truth if IPC is unavailable.

## Platform Lifecycle

### Windows

Do not use NSWS's current HKCU Run entry unchanged. HKCU Run starts at logon but does not supervise a crash.

Use a current-user Windows Task Scheduler definition with:

- An interactive user token
- A logon trigger
- An absolute path to `vb.exe`
- The `--job-daemon` argument
- `MultipleInstancesPolicy=IgnoreNew`
- Restart-on-failure or a repeating trigger that restarts the task if the process exits
- No stored password
- No `SYSTEM` account
- No elevation requirement

The interactive token is required because Automations open terminal windows on the user's desktop.

### Linux

Use a `systemd --user` service with:

- `Restart=on-failure`
- A short restart delay
- An absolute `ExecStart`
- A stable working directory
- User-owned state and logs

Normal desktop login supplies the user service manager. Running after logout may require administrator-enabled lingering, but VBD should not promise useful terminal launches without a graphical user session.

### macOS

Use a per-user LaunchAgent with:

- `RunAtLoad=true`
- `KeepAlive=true`
- An absolute executable path and argument array
- User-owned stdout/stderr log paths
- Installation in the current user's `~/Library/LaunchAgents`

### Instance identity

Mutex, pipe, task/unit, and LaunchAgent names must be scoped to VibeRails and the current user. A process belonging to one operating-system user must not block or control another user's VBD instance.

## API and UI

Add authenticated, root-backend-only lifecycle endpoints. Candidate routes:

```text
GET    /api/v1/jobs/demon
POST   /api/v1/jobs/demon/install
POST   /api/v1/jobs/demon/start
POST   /api/v1/jobs/demon/stop
POST   /api/v1/jobs/demon/restart
POST   /api/v1/jobs/demon/repair
DELETE /api/v1/jobs/demon
```

Suggested status model:

- `NotInstalled`
- `InstalledStopped`
- `Running`
- `NeedsRepair`
- `Unavailable`
- `Error`

The Automations UI should include an explicit setting:

> **Run Automations when VibeRails is closed**

Installation must be opt-in because it modifies per-user operating-system startup. The UI should show:

- Installed/running state
- Current VBD version and PID
- Last successful scheduler cycle
- Whether VBD currently owns the scheduler lease
- Last lifecycle or scheduler error
- Platform-specific limitations
- Start, Stop, Repair, and Remove actions where appropriate

If enabled scheduled Automations exist while VBD is not installed, the page should explain that they run only while an active VibeRails root backend is open.

## Logging and Observability

VBD should write to a dedicated rolling log, for example:

```text
~/.vibe_rails/logs/vbd-.log
```

Log at least:

- Process start, version, PID, and process role
- Successful or failed lifecycle registration
- Scheduler lease acquisition, loss, and release
- Cycle start/completion summaries
- Number of schedules enqueued
- Number of queued runs launched
- Reaped runs and stalled launches
- IPC errors and version mismatches
- Graceful and ungraceful shutdown indicators

Do not duplicate full session output into VBD logs; the existing session/run logging remains authoritative.

## Installer and Update Safety

The current release installers overwrite files under `~/.vibe_rails`. On Windows, a running `vb.exe` can prevent replacement of the executable. VBD-aware updates are therefore a release requirement, not optional polish.

Before replacing application files, installers should:

1. Detect whether VBD is installed and running.
2. Record the installed/running state.
3. Request a graceful VBD shutdown.
4. Confirm the old process has exited.
5. Extract into a staging directory and validate required files.
6. Replace application files.
7. Repair the OS registration so it points at the stable installed executable.
8. Restart VBD when it was previously running.
9. Report a clear recovery command if restart fails.

The Windows and Unix installers must both support this flow. A failed update must not silently leave VBD registered against a missing or temporary executable.

## Legacy Migration

VibeRails previously installed a per-minute OS task that invoked `vb --job-tick`. Current builds intentionally keep `--job-tick` as a no-op compatibility tombstone.

VBD installation should:

- Detect the legacy `VibeRailsJobs` task/unit/LaunchAgent
- Remove or migrate it safely
- Keep the `--job-tick` tombstone for old installations that have not yet been repaired
- Never allow both the legacy tick and VBD to launch work

## Security Requirements

- VBD runs as the current user only.
- Installation must not require or request administrator/root privileges.
- Never install as Windows `SYSTEM`, a systemd system service, or a macOS LaunchDaemon.
- IPC must be current-user-only and must not support arbitrary shell commands.
- Persist absolute executable paths and structured argument lists; do not build shell command strings from untrusted values.
- Validate that installation targets a stable, user-owned application directory rather than a temporary build/download directory.
- Preserve the existing Environment/project ownership checks before launching an Automation.
- Preserve existing CustomArgs validation and workspace path containment.

## Failure Behavior

| Failure | Expected behavior |
| --- | --- |
| Dashboard closes normally | VBD continues polling and launching due work. |
| Dashboard and VBD run together | SQLite lease selects one scheduler; no duplicate launch. |
| VBD crashes | OS supervisor restarts it; stale lease expires safely. |
| Computer sleeps | No work runs during sleep; one overdue run is queued per due trigger after resume. |
| User logs out | No guarantee of visible terminal execution until an interactive session returns. |
| Terminal launch never claims its run | Existing stalled-launch grace marks the run failed with an actionable message. |
| Job process dies | Existing reaper marks the run interrupted and frees overlap/capacity. |
| VBD is an older version | Dashboard reports `NeedsRepair`; durable scheduling data remains untouched. |
| VBD IPC is unreachable | Queue writes still succeed; local scheduler works when the dashboard is open. |
| Update begins while VBD is running | Installer stops VBD, replaces files, repairs registration, and restarts it. |

## Implementation Phases

### Phase 0 - Normalize the reusable NSWS layer

Deliverables:

- Select the in-tree project/package structure
- Add license and metadata
- Separate generic lifecycle/IPC concerns from CronHost's static `JobRegistry`
- Make the registered executable, arguments, working directory, service names, and data directory configurable
- Fix current-user instance isolation
- Add unit tests for quoting, service definitions, state detection, and IPC limits

Exit criteria:

- A small test executable can install, start, query, stop, restart, and uninstall itself for the current user.
- The reusable layer does not require a compiled CronHost job registry.

### Phase 1 - Build the lean VBD process host

Deliverables:

- Add early `--job-daemon` dispatch
- Add `JobDaemonProcessHost`
- Extract shared Automation runtime DI registration
- Start `JobSchedulerHostedService` without Kestrel or dashboard services
- Add dedicated VBD logging
- Add single-instance protection

Exit criteria:

- Running `vb --job-daemon` manually launches due VibeRails Automations from `state.db`.
- The process does not bind an HTTP port or open a browser.
- A simultaneously running dashboard does not cause duplicate runs.

### Phase 2 - Add VBD control and health IPC

Deliverables:

- Implement `PING`, `STATUS`, `KICK`, and `SHUTDOWN`
- Add version/protocol negotiation
- Add daemon health snapshots
- Send cross-process kicks after manual, retry, and commit-triggered enqueue operations

Exit criteria:

- Newly queued work wakes the active scheduler promptly.
- A dead or mismatched VBD is reported accurately without blocking queue writes.

### Phase 3 - Add operating-system lifecycle management

Deliverables:

- Windows interactive Task Scheduler integration
- Linux `systemd --user` integration
- macOS LaunchAgent integration
- Install/start/stop/restart/repair/uninstall service abstraction
- Legacy scheduler cleanup

Exit criteria:

- VBD starts at user login on every supported platform.
- It is restarted after an unexpected exit where the platform supports supervision.
- It can launch an Automation terminal onto the logged-in user's desktop.

### Phase 4 - Add authenticated API and UI

Deliverables:

- Lifecycle/status DTOs and routes
- Automations-page background execution setting
- Status, diagnostics, repair, and removal controls
- Clear platform and session limitations
- Warning when scheduled work exists without VBD

Exit criteria:

- A user can manage VBD without using hidden CLI commands.
- UI state reflects actual OS registration and live IPC reachability.

### Phase 5 - Make installers and releases VBD-aware

Deliverables:

- Stop/replace/repair/restart update flow in `Scripts/install.ps1`
- Equivalent flow in `Scripts/install.sh`
- Release packaging of every new assembly/content file
- Native AOT publish checks for all supported RIDs
- Upgrade and rollback documentation

Exit criteria:

- Upgrading a machine with VBD running succeeds without locked-file or stale-registration failures.
- VBD returns to its previous installed/running state after a successful upgrade.

### Phase 6 - Platform soak and rollout

Deliverables:

- Feature flag or preview label for the first release
- Long-running soak tests
- Crash/restart and sleep/resume verification
- Troubleshooting documentation
- Telemetry-free local diagnostics sufficient for support

Exit criteria:

- No duplicate launches across repeated dashboard/VBD restarts.
- No permanent scheduler wedges after killed job or host processes.
- Platform limitations are documented and visible before installation.

## Test Plan

### Unit tests

- Process-role classification excludes VBD from root backend behavior
- Minimal DI graph resolves every Automation launch dependency
- OS command/argument quoting, including paths with spaces
- Per-user mutex and pipe isolation
- IPC authorization, timeouts, size limits, and protocol mismatch
- Install/start/stop/restart/uninstall idempotency
- Registration repair when executable path or version changes
- VBD status mapping

### Integration tests

- VBD alone enqueues and launches a due schedule
- Dashboard and VBD contend for one scheduler lease
- Lease loss cancels the losing host's remaining launch batch
- Manual and retry runs wake VBD
- Commit-triggered queued runs are drained without the dashboard
- Launch cap remains three across processes
- Per-job overlap guard remains effective
- Dead job processes are reaped
- Stalled launches are failed after the grace period
- Workspace modes work through the shared launch pipeline
- Run/session recording remains visible in Automation history
- Closing VBD does not corrupt or discard queued work

### Platform end-to-end tests

- Install from a stable release directory
- Start at login
- Close the dashboard and observe a scheduled run
- Lock and unlock the desktop
- Sleep and resume across a due time
- Kill VBD and verify restart/takeover
- Run the dashboard concurrently
- Upgrade while VBD is running
- Repair a stale executable registration
- Uninstall without deleting jobs, runs, sessions, or user configuration

### Release verification

- `dotnet test`
- Native AOT publish for `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64`
- Smoke-test lifecycle commands against published binaries
- Verify install archives contain all VBD dependencies
- Verify no new trimming/AOT warnings are introduced

## Acceptance Criteria

VBD is ready for general release when all of the following are true:

1. A scheduled Automation runs with the dashboard closed.
2. It uses the same Environment, prompt, custom arguments, workspace mode, timeout, and terminal/session recording as a dashboard-launched Automation.
3. Dashboard and VBD concurrency cannot create duplicate runs or exceed existing launch limits.
4. Queued work survives VBD and dashboard restarts.
5. VBD is installed and controlled entirely as the current user.
6. Windows, Linux, and macOS lifecycle implementations are supervised appropriately.
7. The UI accurately distinguishes installed, running, stopped, broken, and unsupported states.
8. Updates safely stop and restart VBD without leaving a locked executable or stale registration.
9. Legacy `--job-tick` registrations are removed or made harmless.
10. All job, scheduler, installer, IPC, platform, and Native AOT verification passes.

## Initial Validation Completed

During feasibility review:

- NSWS restored and built successfully with zero warnings and zero errors.
- All 15 NSWS self-checks passed.
- The relevant VibeRails Jobs, scheduler lease, and overlap tests passed: 81/81.
- The VibeRails working tree remained unchanged during the review.
- NSWS Native AOT publishing could not be tested on the review machine because the Visual C++ build tools were not installed; this remains a required release check.

## Recommended First Implementation Slice

Begin with Phases 0 and 1 behind a development-only flag:

1. Bring the reusable NSWS lifecycle primitives into a reproducible in-tree project.
2. Add `vb --job-daemon` and the minimal shared Automation service graph.
3. Run VBD manually, with no OS installation or UI yet.
4. Prove that a due Automation launches correctly while the dashboard is closed.
5. Prove that opening the dashboard concurrently does not duplicate the run.

That slice validates the central architecture before changing operating-system startup, installers, or the user interface.
