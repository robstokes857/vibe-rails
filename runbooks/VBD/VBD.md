# VibeRails Demon (VBD) runbook

VBD is the preview, current-user background host for VibeRails Automations. It runs the existing
SQLite-backed scheduler through the same Environment, workspace, terminal, timeout, cancellation,
session-recording, and history pipeline used by an open dashboard. It does not create a second job
registry or copy schedule state outside `~/.vibe_rails/state.db`.

## User-visible limits

- VBD is opt-in from **Automation → Run Automations when VibeRails is closed**.
- The user must remain logged in with an interactive desktop because Automations open visible native
  terminal windows.
- Work does not run while the computer is off or asleep. After resume, the existing schedule logic
  queues one overdue occurrence and advances to the next future occurrence.
- There is no promise of visible terminal execution after logout. Linux user lingering does not
  change that product limit.
- VBD runs with the same operating-system permissions and CLI credentials as the signed-in user.
  It is not a sandbox or privileged system service.

## Local diagnostics

The Automation page is the primary diagnostic surface. It shows registration state, live process
version and PID, uptime, last successful scheduler cycle, lease ownership, and the last scheduler or
lifecycle error. VBD has no telemetry and sends no health data away from the machine.

The rolling process log is:

```text
~/.vibe_rails/logs/vbd-YYYYMMDD.log
```

An installed release also exposes a hidden, installer-safe status command:

```text
vb --job-daemon-service status --json
```

The JSON distinguishes `NotInstalled`, `InstalledStopped`, `Running`, `NeedsRepair`, `Unavailable`,
and `Error`. A running process can be healthy without owning the scheduler lease when another open
VibeRails root backend currently owns it.

## Recovery

Use the Automation page first. **Repair** rewrites the current-user OS registration to the stable
binary in `~/.vibe_rails`, removes the legacy `VibeRailsJobs` tick registration, and preserves the
previous running/stopped state. **Remove** deletes only the startup registration; it does not delete
Automations, queued runs, sessions, workspaces, or history.

The installer recovery commands are intentionally narrow:

```text
vb --job-daemon-service stop
vb --job-daemon-service repair
vb --job-daemon-service start
```

If status is `NeedsRepair`, verify that the command is being run from the current stable release in
`~/.vibe_rails`, then run `repair`. Registrations from a temporary download, build output, VSIX
directory, or versioned staging directory are rejected as launch targets.

If VBD is installed but unreachable:

1. Read the newest `vbd-*.log` and the error shown on the Automation page.
2. Confirm the configured project, Environment CLI, and credentials are still available to the
   signed-in user.
3. Run **Repair**, then **Start**.
4. If the old process is stuck, stop it through the Automation page or the maintenance command and
   retry. Durable queued rows remain in `state.db` throughout recovery.

## Platform registration

- **Windows:** a current-user Task Scheduler task with an interactive token, logon trigger,
  `IgnoreNew`, restart-on-failure, no stored password, and no elevation.
- **Linux:** a `systemd --user` service with `Restart=on-failure` and an absolute `ExecStart`.
- **macOS:** a per-user LaunchAgent in `~/Library/LaunchAgents` with `RunAtLoad` and `KeepAlive`.

Never convert VBD into a Windows `SYSTEM` task, system-level systemd unit, or macOS LaunchDaemon.

## Upgrade and rollback

Release installers record whether VBD is installed/running, request graceful shutdown, wait for the
old process to exit, validate a privately staged archive, overlay application files without removing
user data, repair the stable registration, and restart only when it was running before the update.
If restart fails, the installer prints the exact repair/start commands and leaves durable Automation
data untouched.

For rollback, install the previous complete release payload through the same installer flow. A
running daemon whose application or IPC protocol version differs from the dashboard reports
`NeedsRepair`; repairing/restarting loads the version currently present in `~/.vibe_rails`.

## Preview soak checklist

Run this checklist on every supported release RID before removing the Preview label. Keep the
dashboard and VBD logs for the duration; no external telemetry is needed.

1. Install from a stable release directory and enable VBD from the Automation page.
2. Configure short interval Automations in disposable repositories and run for at least 24 hours.
3. Repeatedly open/close the dashboard while runs queue; confirm one scheduler lease owner and no
   duplicate run IDs or terminal launches.
4. Kill VBD and confirm the OS supervisor restarts it after the stale lease can be taken over.
5. Sleep across multiple due times, resume, and confirm one overdue run per due trigger rather than
   replaying every missed interval.
6. Lock/unlock, then log out/in, and verify the documented interactive-session limits.
7. Exercise manual, retry, before-commit, and after-commit enqueue paths with the dashboard closed;
   each durable row should wake VBD promptly.
8. Kill a job process and force a stalled launch; verify reaping, overlap release, and actionable run
   history without a permanent scheduler wedge.
9. Run the installer while VBD is active; confirm stop, replacement, repair, and conditional restart.
10. Remove VBD and confirm all Jobs, runs, sessions, terminal recordings, and configuration remain.

Record the OS version, RID, VibeRails version, sleep/logout results, crash-restart latency, duplicate
count, and any repair needed. Platform soak is a release qualification activity and must not be run
automatically from a developer checkout against the user's real `state.db`.
