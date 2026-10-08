# Python Scripts

This folder owns VibeRails' signed single-file script library. The names still say Python (the
classes, `/api/v1/python-scripts`, `--run-python-script`), but since VIBE-56 a script is pwsh
(`.ps1`), bash (`.sh`) or python (`.py`), and the extension picks the interpreter.

## User workflow

1. Open **Automation → Scripts** and create or register a `.ps1`, `.sh` or `.py` file.
   New script selects a runtime and a destination (default `~/.vibe_rails/scripts/UserScripts`,
   or a local folder selected with the shared file picker). Add from disk registers the original
   path without copying. Both forms offer a display name, Global/Repo scope and PIN on every run.
2. Sign the exact script version with the user's Python signing PIN.
3. Choose **Run** for the captured run window, with argument rows and optional standard input,
   or **Run in terminal** for interactive scripts. The terminal path takes the script name and
   an optional required run PIN.
4. Edit scripts in the workbench, which pairs Monaco with an agent terminal. Changed bytes need
   signing again before either run path can execute them.

Only explicit registrations in `~/.vibe_rails/user_script_library.json` appear. No folder is scanned;
legacy scripts and approvals are not imported, moved or deleted. `scripts` outside `UserScripts`
is reserved for internal app scripts and cannot be registered. Removing a library entry preserves
its file. Repo scope uses the same server-derived canonical project root as the Automation page;
external Repo files must live in that root, while managed UserScripts files can belong to it.
Global entries appear in all instances. Metadata uses a separate atomic file so older versions
writing the signing document cannot erase it; the existing cross-process signing lock serializes
both documents. No state DB schema changes or backfill are required.

Each registration has a stable ID. APIs accept it in the legacy `name` field; list responses retain
`name` as the actual filename and add `id`, `displayName`, `scope`, `projectPath`. A unique visible
filename is accepted by local callers for convenience. The frontend normalizes the ID into its
internal `name` key and retains `fileName` for runtime and display. The nav launcher uses the ID,
not the display label. Display/scope changes do not grant approval.

Scripts run with their own folder as working directory, from a verified temporary copy alongside
the original, so `$PSScriptRoot`, Python script-relative paths and relative subprocess paths use
the original directory. The temporary filename differs from the original. Python sibling imports
are possible but their contents, like installed packages or programs a script invokes, are not
covered by the single-file signature. Every path component is checked for links/reparse points
on authoring, signing and execution.

## Runtimes

`PythonScriptService.RuntimeFor` maps the lower-case extension to `JobScriptRuntime`; the name rule
accepts only `.py`, `.ps1` and `.sh`, and the frontend mirrors it in `script-runtimes.js` (a node
test compares the two patterns). Python keeps PyBridge discovery (venv → conda → PATH). pwsh and
bash resolve through `JobExecutableResolver`, the same lookup repository Automation scripts use
(Git Bash on Windows, never the System32 WSL bridge), and the missing-interpreter wording comes
from `AutomationScriptService.MissingRuntimeMessage`.

- **Captured runs** reuse the PyBridge runner with the interpreter path swapped in, so argv,
  stdin, the ten-minute timeout and output capture are identical for all three. pwsh gets
  `-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <copy>`; the terminal path
  drops `-NonInteractive` so `Read-Host` works there.
- **The verified copy keeps the extension** (pwsh refuses `-File` on anything but `.ps1`). Bash
  gets the canonical text the approval was computed over (BOM removed, LF line endings), because
  bash reads every `\r` as part of a command; it is passed as a relative slash path
  (`AutomationScriptService.ToBashPath`). pwsh and python run the signed bytes unchanged.
- New approvals mix in the registration ID and absolute path; a rename needs signing again.
  Legacy approvals are retained but never matched to new registrations.

## Architecture and invariants

- `PythonScriptService.cs` owns files, canonical hashing, PIN approvals, and verified execution.
- `PythonScriptSignProcessHost.cs` implements the interactive `vb --sign-script` helper.
- `PythonScriptRunProcessHost.cs` executes verified bytes inside an inherited console/PTY.
- `Routes/PythonScriptRoutes.cs` provides the authenticated list, authoring, signing, and run API.
- `wwwroot/js/modules/python-scripts-controller.js` owns shared lifecycle and signing flows.
- `python-run-window.js` sends discrete argument values and optional stdin, then displays stdout,
  stderr, exit status, and structured JSON return values.
- `python-script-workbench.js` owns the editor and docked agent terminal.

Signing pins the canonical SHA-256 of strict UTF-8 bytes (BOM removed, line endings normalized,
registration ID and absolute file path included). Execution rechecks that hash and runs a verified copy (canonical text for bash). An edit, rename,
or revoke must never silently approve different code. PINs are never stored as plaintext.
Arguments use PyBridge arrays / `ProcessStartInfo.ArgumentList`, never shell concatenation.

Run PIN requirements are optional globally and per script. New entries can require a PIN before any approval exists; changing an existing requirement requires the signing PIN through settings or the existing run-pin API. Every signing-document mutation,
including changing either requirement, takes the existing cross-process write lock before its
instance semaphore and read/modify/write. Captured and interactive dashboard runs collect a
required signing PIN for each launch; cancellation starts nothing. PINs are never remembered
with arguments/stdin or retained in controller/window state. New request DTOs require an
`AppJsonSerializerContext` registration and a real HTTP binding regression.

Authoring does not take a PIN or create approvals. Saves require the raw-content version read by
the editor; stale saves must not overwrite newer bytes. Import paths reject network/device paths
and links. Runtime discovery is lazy: listing and signing need no interpreter, and only a `.py`
run probes for Python.

## Removed MCP integration — 2026-09-18

Custom Python MCP tools and `python_script_signing_help` were removed at the owner's request
because agents already have their own code-execution tools. Neither HTTP nor stdio registers
Python tools or a dynamic call handler. The three `/api/v1/python-scripts/mcp` configuration
routes, exposure switch, and MCP parameter editor are gone.

Legacy `~/.vibe_rails/python_script_mcp.json` files are ignored, not deleted. Ordinary script
files, signatures, automations, and human-initiated runs remain. The run window accepts argument
rows and stdin; it no longer derives typed fields from MCP configuration. Remembered legacy typed
inputs suppress automatic execution on opening, so the user can review the argument rows first.

## Tests

Keep the Python signing, script service, shell runtime (`PythonScriptShellRuntimeTests`, which
also runs real pwsh and bash when they are installed), process-host, route, workbench, and
run-window suites green. MCP transport tests verify that removed Python tool names are unavailable.
