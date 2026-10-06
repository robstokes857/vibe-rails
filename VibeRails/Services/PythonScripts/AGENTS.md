# Python Scripts

This folder owns VibeRails' signed single-file script library. The names still say Python (the
classes, `/api/v1/python-scripts`, `--run-python-script`), but since VIBE-56 a script is pwsh
(`.ps1`), bash (`.sh`) or python (`.py`), and the extension picks the interpreter.

## User workflow

1. Open **Automation → Scripts** and create or import a `.ps1`, `.sh` or `.py` file. New script
   asks which runtime (pwsh, bash or python) and keeps that choice and the extension in step.
2. Sign the exact script version with the user's Python signing PIN.
3. Choose **Run** for the captured run window, with argument rows and optional standard input,
   or **Run in terminal** for interactive scripts. The terminal path takes the script name and
   an optional required run PIN.
4. Edit scripts in the workbench, which pairs Monaco with an agent terminal. Changed bytes need
   signing again before either run path can execute them.

Scripts live in `~/.vibe_rails/scripts`. They run with that directory as their working directory.
A script is one self-contained UTF-8 file; Python imports must be from the standard library or
installed packages, not sibling scripts.

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
- Signatures, hashes and stored approvals are unchanged: the canonical hash already mixes in the
  file name, so existing `.py` approvals stay valid and a `.ps1`/`.sh` rename always re-signs.

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
file name included). Execution rechecks that hash and runs a verified copy (canonical text for bash). An edit, rename,
or revoke must never silently approve different code. PINs are never stored as plaintext.
Arguments use PyBridge arrays / `ProcessStartInfo.ArgumentList`, never shell concatenation.

Run PIN requirements are optional globally and per script. Every signing-document mutation,
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
