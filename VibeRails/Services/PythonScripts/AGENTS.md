# Python Scripts

This folder owns VibeRails' single-file Python automation feature.

## User workflow

1. Open **Automation → Python scripts** and create or import a `.py` file.
2. Sign the exact script version with the user's Python signing PIN.
3. Choose **Run** for the captured run window, with argument rows and optional standard input,
   or **Run in terminal** for interactive scripts. The terminal path takes the script name only.
4. Edit scripts in the workbench, which pairs Monaco with an agent terminal. Changed bytes need
   signing again before either run path can execute them.

Scripts live in `~/.vibe_rails/scripts`. They run with that directory as their working directory.
A script is one self-contained UTF-8 file; imports must be from the standard library or installed
packages, not sibling scripts.

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
file name included). Execution rechecks that hash and runs a verified copy. An edit, rename,
or revoke must never silently approve different code. PINs are never stored as plaintext.
Arguments use PyBridge arrays / `ProcessStartInfo.ArgumentList`, never shell concatenation.

Authoring does not take a PIN or create approvals. Saves require the raw-content version read by
the editor; stale saves must not overwrite newer bytes. Import paths reject network/device paths
and links. Runtime discovery is lazy: listing and signing do not require Python to be installed.

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

Keep the Python signing, script service, process-host, route, workbench, and run-window suites
green. MCP transport tests verify that removed Python tool names are unavailable.
