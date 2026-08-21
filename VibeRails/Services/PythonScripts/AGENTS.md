# Python Scripts and MCP Tools

This folder owns VibeRails' single-file Python automation feature and the optional mapping from a
user-approved script to a dynamic MCP tool.

## User workflow

1. Open **Automation → Python scripts** and create or import a `.py` file.
2. Sign the exact script version with the user's Python signing PIN.
3. Turn on the row's **MCP** switch.
4. In the MCP configuration dialog, enter:
   - a unique MCP tool name;
   - a description that tells an agent when to call it;
   - the signing PIN (required whenever exposure is enabled or edited);
   - zero or more script parameters.
5. For every parameter, define the MCP input name, description, JSON type, required/default
   behavior, and how it reaches Python:
   - **Positional value** appends only the value to `sys.argv`.
   - **Named option** appends the configured flag and then its value, for example
     `--output report.json`.
   - A boolean named option behaves like a normal `argparse` `store_true` flag: the flag is
     appended when the value is `true` and omitted when it is `false`.
6. Open **MCP Explorer → Local**. Enabled scripts appear under **Python script tools**, separate
   from VibeRails' built-in tools.

Turning the switch off removes the dynamic tool immediately. Renaming a script carries its MCP
mapping to the new file name; deleting a script removes the mapping. Editing script bytes does not
remove the mapping, but every invocation fails closed until the changed script is signed again.

## Python authoring contract

- Scripts run with the scripts directory as their working directory.
- MCP values are passed with `ProcessStartInfo.ArgumentList` / PyBridge argument arrays. Never
  concatenate them into a shell command.
- The supported MCP input types are `string`, `integer`, `number`, and `boolean`.
- Optional positional inputs without defaults must be last. Otherwise an omitted value would shift
  the meaning of later positional arguments.
- Named options should use normal flags such as `--output` or `-o`.
- MCP execution captures stdout and stderr. Exit code `0` is a successful tool result; a non-zero
  exit code or timeout is returned with MCP `isError: true`.

Example script:

```python
import argparse

parser = argparse.ArgumentParser()
parser.add_argument("source")
parser.add_argument("--limit", type=int, default=10)
parser.add_argument("--verbose", action="store_true")
args = parser.parse_args()

print(f"Processing {args.source} (limit={args.limit}, verbose={args.verbose})")
```

Configure it with:

| MCP input | Type | Required/default | Pass as | Python flag |
|---|---|---|---|---|
| `source` | string | required | positional | — |
| `limit` | integer | default `10` | named option | `--limit` |
| `verbose` | boolean | optional | named option | `--verbose` |

## Architecture

- `PythonScriptService.cs` owns script files, canonical hashing, signing, and verified execution.
- `PythonScriptMcpService.cs` owns `python_script_mcp.json`, configuration validation, JSON schema
  generation, MCP-input-to-argv mapping, and dynamic calls.
- `Services/Mcp/PythonScriptMcpServerExtensions.cs` adds list/call handlers alongside the static MCP
  tool collection. Both HTTP and stdio registrations must call `WithPythonScriptTools()`.
- `Routes/PythonScriptRoutes.cs` exposes `GET|PUT|DELETE /api/v1/python-scripts/mcp`.
- `wwwroot/js/modules/python-scripts-controller.js` owns the row switch and configuration dialog.
- `wwwroot/js/modules/mcp-controller.js` renders dynamic scripts in their own Explorer section.

Configuration is global per VibeRails install, like the scripts folder itself. It is stored at
`~/.vibe_rails/python_script_mcp.json`; the signing PIN is never stored in that document.

## Security invariants

- Enabling or editing MCP exposure must call `AuthorizeMcpExposureAsync`: the script must match its
  approved hash and the user must enter the signing PIN.
- Disabling exposure does not require a PIN because it only removes capability.
- MCP never accepts a PIN and never signs or approves script bytes.
- Every MCP invocation must call the signed `PythonScriptService.RunAsync` path so an edit after
  exposure fails closed.
- Keep `additionalProperties: false` in generated input schemas and reject undeclared inputs at
  runtime.
- Treat exposed scripts as potentially destructive/open-world MCP tools. The annotations must not
  claim read-only or idempotent behavior unless the user-facing model is expanded to capture and
  enforce those promises.

## Tests

- `Tests/Services/PythonScriptMcpServiceTests.cs` covers authorization, persistence, schemas, argv
  mapping, and fail-closed invocation.
- `Tests/wwwroot/js/python-scripts-controller.test.mjs` covers the switch and parameter editor.
- `Tests/wwwroot/js/mcp-controller.test.mjs` pins the separate Explorer section.
- Keep the existing Python signing, route, workbench, HTTP MCP, and stdio MCP suites green.

## Vibe Rails Rules

---

*Last checked: 2026-08-21 by Codex*
