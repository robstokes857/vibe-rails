# Python Scripts and MCP Tools

This folder owns VibeRails' single-file Python automation feature and the optional mapping from a
user-approved script to a dynamic MCP tool.

## User workflow

1. Open **Automation → Python scripts** and create or import a `.py` file.
2. Sign the exact script version with the user's Python signing PIN.
3. Turn on the row's **MCP** switch.
4. In the MCP configuration dialog, fill in three sections:
   - **Identity** — a unique MCP tool name, and a description that tells an agent when to call it.
   - **Behavior** — what running the script does. Nothing is preselected and the dialog will not
     save until it is answered. See *Declared behavior* below.
   - **Parameters** — zero or more script parameters.
5. Saving asks for the signing PIN in a separate prompt, after the form is complete. The PIN is
   required whenever exposure is enabled or edited, is never a field inside the configuration form,
   and is never stored.
6. For every parameter, define the MCP input name, description, JSON type, required/default
   behavior, and how it reaches Python:
   - **Positional value** appends only the value to `sys.argv`.
   - **Named option** appends the configured flag and then its value, for example
     `--output report.json`.
   - A boolean named option behaves like a normal `argparse` `store_true` flag: the flag is
     appended when the value is `true` and omitted when it is `false`.
7. Open **MCP Explorer → Local**. Enabled scripts appear under **Python script tools**, separate
   from VibeRails' built-in tools.

Turning the switch off removes the dynamic tool immediately. Renaming a script carries its MCP
mapping to the new file name; deleting a script removes the mapping. Editing script bytes does not
remove the mapping, but every invocation fails closed until the changed script is signed again.

## Declared behavior

MCP advertises four behavior hints per tool. VibeRails does not guess them: the script's author —
the user, or the agent that wrote the script — declares what running it does, and `ToTool` derives
the hints from that declaration. Three controls produce four hints:

| Control | Choice | readOnly | destructive | idempotent | openWorld |
|---|---|---|---|---|---|
| Radio | Reads and reports only | `true` | `false` | `true` | — |
| Radio | Creates or updates | `false` | `false` | — | — |
| Radio | Overwrites or deletes | `false` | `true` | — | — |
| Checkbox | Running it again changes nothing more | — | — | declared | — |
| Checkbox | Reaches the network or outside services | — | — | — | declared |

Read-only forces idempotent on (a script that changes nothing cannot change more on a second run),
so that checkbox is checked and disabled for that choice.

All four hints are always sent explicitly. Per the MCP spec an unspecified `destructiveHint` means
"assume destructive" and an unspecified `idempotentHint` means "assume not idempotent", so omitting
them would misrepresent a read-only tool to any client that does not also read `readOnlyHint`.

Storage uses the product's vocabulary rather than the protocol's — `behavior` is one of
`read-only` / `additive` / `destructive`, alongside `repeatSafe` and `reachesNetwork`. A new
protocol hint is then a mapping change, not a stored-schema migration.

Every dynamic tool also publishes where its source lives. `ToTool` appends the absolute script path
to the description and repeats it in `_meta.scriptPath`, so a calling agent can read the script and
judge it before running anything. That disclosure is appended at tool-build time and does not
consume the author's 500-character description budget.

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
- `wwwroot/js/modules/mcp-controller.js` renders dynamic scripts in their own Explorer section,
  and badges each tool with the behavior hints read back off the wire (`Routes/McpRoutes.cs`
  copies them from `McpClientTool.ProtocolTool.Annotations`, so the badges show what a caller
  is actually told rather than what local configuration says).

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
- Annotations carry the author's declaration, never a guess or a default. `ValidateConfiguration`
  rejects a missing or unknown `behavior`, so nothing reaches the store undeclared.
- `ValidateStoredConfiguration` must keep carrying every declaration field. It rebuilds a request
  from the stored record on every `ListToolsAsync`, so a field it forgets is silently dropped from
  the advertised tool rather than failing loudly.
- The declaration is a claim by whoever wrote the script, not something VibeRails can verify, and
  the MCP spec says clients must not make security-critical decisions on annotations alone. A tool
  declared read-only still runs with the user's full privileges. The load-bearing controls remain
  the signing hash gate and the published source path that lets a caller read the script first.

## Tests

- `Tests/Services/PythonScriptMcpServiceTests.cs` covers authorization, persistence, schemas, argv
  mapping, fail-closed invocation, the behavior-to-annotation mapping, the rejection of an
  undeclared behavior, and the stored round trip that keeps declarations intact.
- `Tests/wwwroot/js/python-scripts-controller.test.mjs` covers the switch and parameter editor.
- `Tests/wwwroot/js/mcp-controller.test.mjs` pins the separate Explorer section.
- Keep the existing Python signing, route, workbench, HTTP MCP, and stdio MCP suites green.

---

*Last checked: 2026-08-23 by Claude*
