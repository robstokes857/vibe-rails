# AGENTS.md - VibeRails Project Documentation

## Runbooks and diagnostic helpers

Runbooks, investigation notes, and diagnostic Python helpers live in the private
`vibe-books` repository. With sibling checkouts, start at
[the topic index](../vibe-books/INDEX.md). Keep product code, regression tests,
build/install scripts, and component documentation in this repository.

[API_SEC.md](API_SEC.md) stays here as the API security contract and review gate.
Read it before changing API exposure, authentication, or production listeners;
record security violations in `SECURITY_ERROR.md` for the owner to review.

## Database change policy

Opening a new version must just work. Required schema setup is automatic; never require a
migration command, opt-in flag, manual backup, or closing other VibeRails instances to upgrade.
Prefer additive changes and preserve older-version compatibility where possible, because
different versions can run on the same machine.

**Only breaking database changes require separate owner sign-off.** Compatible additive changes
such as new tables, nullable columns or indexes do not require sign-off or a Board attention flag.
Keep schema snapshots and relevant compatibility tests current. Breaking changes that remove or
rewrite stored data, or invalidate older versions' read/write assumptions, require owner review
before proceeding unless that exact change is already explicitly authorized. This is a contributor
review requirement, not an acknowledgement or manual step during application startup.

**Debugging uses the normal application state database, `~/.vibe_rails/state.db`.** Do not
reintroduce a separate debug/development database, a build- or branch-dependent data directory,
or a permission flag to let Debug builds use the normal database. Do not redirect the app to a
copy to make unsafe code seem acceptable. If a change cannot safely use the running database,
fix the change; an alternate runtime database is not the solution. Automated tests may still
use disposable fixtures; that does not authorize a separate database for application debugging.

Board state lives in `~/.vibe_rails/board.db` in all configurations, including Debug. This is a
component split, not a development database. Legacy Board tables in `state.db` remain untouched;
the current application does not read, write, migrate or synchronize them.

**Removing a feature does not authorize deleting its stored data, tables, or columns.** Stop
reading and writing the retired fields/tables in the current code and leave them in place.
Unused schema is acceptable technical debt; do not prioritize its cleanup over requested work.
Only perform destructive schema cleanup, historical-data conversion, or backfills when the
owner explicitly requests that work. The existing Board cleanup was accepted for that release;
it is not a precedent for future feature removals.

For implementation details, read [SQLite instructions](VibeRails.Data.Sqlite/AGENTS.md) and the
[database reference](VibeRails.Data.Sqlite/DB/AGENTS.md). This policy supersedes contradictory
historical migration plans or runbooks.

## Board attention flags

Reserve `flagged=true` for important unresolved issues that require the owner's decision or
intervention: a breaking database change awaiting authorization, a confirmed security or data-loss
problem, or a consequential blocker the agent cannot resolve within the authorized scope.
Explain the concrete issue and the decision or action needed in a card comment.
For an agent flag, pass `flagReason` with `flagged=true` to `update_board_card`; the tool
requires it and saves the red attention comment atomically with the flag.

Routine progress, completion, moving to Review, opening a PR, and compatible additive schema
changes do not warrant an attention flag. Use comments and the normal review workflow for those.
Clear an agent-set flag once its reason is resolved; do not clear a flag with another unresolved
reason. An already-authorized change does not need another approval or flag for the same decision.

## Terminology Note

**"Web UI Chat"** refers to the xterm.js-based terminal interface where users interact with CLI tools (Claude, Codex, Antigravity) through a browser-based terminal emulator. This is NOT a separate chat UI - it's the PTY-backed terminal that runs actual CLI sessions.

## Project Overview

**VibeRails** is a desktop/web control panel for AI coding CLIs (Claude, Codex, Antigravity,
Copilot, OpenCode, Grok): `vc.rules.md` rule enforcement (WARN/COMMIT/STOP), per-CLI
environments, git-clone sandboxes, session logging, Automations, the kanban Board, and an
in-process MCP server. Stack: .NET 10 / ASP.NET Core Slim with Native AOT, SQLite in WAL mode,
vanilla JavaScript + Bootstrap 5 + xterm.js, xUnit v3. Live site: [https://viberails.ai/](https://viberails.ai/).

The architecture reference (project layout, application and process-host modes, component
interaction flows, the service, data, REST and frontend catalogs, design patterns, file locations,
development workflows, troubleshooting, security and performance notes) is in
[docs/architecture.md](docs/architecture.md). Read the section you need rather than the whole
file; it is reference material, not instructions.

## Where to start, by area

| Area | Start here |
| --- | --- |
| Board (UI, API, SQLite, launch, MCP) | [Board contributor guide](VibeRails/Services/Board/AGENTS.md), [architecture and VB-18 review](VibeRails/Services/Board/ARCHITECTURE.md) |
| MCP server and tools | [MCP guide](VibeRails/Services/Mcp/AGENTS.md) |
| Terminal sessions and launch options | [Terminal guide](VibeRails/Services/Terminal/AGENTS.md), [launchers](VibeRails/Services/LlmClis/Launchers/AGENTS.md), [CLI](VibeRails/Services/Cli/AGENTS.md) |
| Storage, schema, migrations | [SQLite instructions](VibeRails.Data.Sqlite/AGENTS.md), [database reference](VibeRails.Data.Sqlite/DB/AGENTS.md), [Board store](VibeRails.Data.Sqlite/Board/AGENTS.md) |
| VCA rules and git hooks | [VCA guide](VibeRails/Services/VCA/AGENTS.md), [git hook installation](docs/git-hooks.md) |
| Python scripts | [Python scripts guide](VibeRails/Services/PythonScripts/AGENTS.md) |
| Frontend modules | [wwwroot guide](VibeRails/wwwroot/AGENTS.md), [code report viewer contract](VibeRails/wwwroot/js/modules/code-report/README.md) |
| Token saver and the local LLM proxy | [TokenSaver README](TokenSaver/README.md) |
| UI and end-to-end tests | [UITests guide](UITests/AGENTS.md) |
| VS Code extension | [extension guide](vscode-viberails/AGENTS.md) |
| API exposure and authentication | [API_SEC.md](API_SEC.md) |

## Rules that are easy to miss

- **There is no background host.** Automations run only while a VibeRails root backend is open:
  no daemon, no operating-system registration, no `--job-daemon` role. The per-user host (VBD)
  and everything around it was deleted on 2026-09-13. Read `vibe-books/deamon/deamon_retro.md`
  before proposing anything that registers VibeRails with the operating system; it records
  mistakes, not a starting point.
- **Board persistence stays behind `IBoardStore`**, including pending lane Automation events, so
  a future shared API-backed store can replace local storage. Local Jobs and terminal history
  remain in `state.db`; queuing an Automation run and acknowledging its Board event are separate
  commits. Card text may reference repository files as `@path` (VB-35); the text is the only
  storage. The Board review records open concurrency and workflow findings; documentation is not
  evidence that they have been fixed.
- **Rule files.** The rules heading is `## Vibe Rails Rules` (`## Vibe Control Rules` is still
  read for older files); the section ends at the next heading and fenced code blocks inside it
  are skipped, so an example of a rule is never itself a rule. Full contract:
  [Services/VCA/AGENTS.md](VibeRails/Services/VCA/AGENTS.md).
- **Launch arguments are argv, never shell text.** Automation scripts, VibeRails child launches
  and CustomArgs go through explicit executables and `ShellArgSanitizer`; do not build command
  strings from user fields.
- **Logging.** The host clears MEL providers; the file sink is the static Serilog logger in
  `Program.cs`, so `builder.Logging.SetMinimumLevel` does not affect `logs/vb-*.log`. A
  Warning-only file sink was tried and reverted because a healthy run and a dead scheduler looked
  identical on disk. The bounded feature journal is `IFeatureLog` (root backend only); see the
  [debug logging notes](docs/architecture.md#debug-logging).
- **MCP tools** are exposed as snake_case (`MyTool` becomes `my_tool`). A new tool needs
  `.WithTools<…>()` in `MapRegisterServices.cs` and a test under `Tests/Services/Mcp/`.
- The old CLI management commands (`vb env`, `vb validate`, `vb hooks`, and so on) are not part
  of the supported surface. Use the Web UI, the VS Code extension, or the REST API.
- **Claude launches go through the TokenSaver proxy** (`ANTHROPIC_BASE_URL`). That disables
  Claude Code's MCP tool search and makes it budget a 200K window for a bare model ID, so the
  proxy env always carries `ENABLE_TOOL_SEARCH=true` and the model catalogs offer `[1m]` IDs.
  Keep those together; see the [TokenSaver README](TokenSaver/README.md#things-that-will-bite-you).

## Build and test

Run from the repository root unless a guide says otherwise.

```bash
dotnet build                                   # development build
dotnet test Tests/Tests.csproj                 # xUnit suite (filter with --filter "FullyQualifiedName~...")
node --test Tests/wwwroot/js/*.test.mjs        # frontend module regressions
npm --prefix UITests run test:e2e              # Playwright end-to-end; see UITests/AGENTS.md
dotnet run --project IntegrationTest           # PTY / terminal / agent-flow integration
dotnet publish -c Release                      # Native AOT release build
.\deploy\build.ps1                             # Windows AOT + Linux AOT via Docker
```

## Contributing

- C#: PascalCase public, camelCase private, nullable reference types on, async/await over
  blocking calls, XML documentation on public APIs.
- Tests: unit tests for new services, integration tests for API endpoints, MCP tools tested
  independently; keep coverage above 80%.
- Pull requests: branch from `main`, implement with tests, update documentation (this file for
  policy and pointers, `docs/architecture.md` for architecture), run `dotnet test`, and submit
  with a clear description.

---

**Last Updated**: 2026-09-27 (trimmed to instructions; reference material moved to `docs/`)
**Maintained By**: Robert Stokes
