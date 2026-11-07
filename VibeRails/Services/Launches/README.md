# Launch definitions and runs

`LaunchService` is the shared admission boundary for HTTP, MCP, Board launches,
Git events and schedules. `ILaunchStore` owns the global catalog in
`~/.vibe_rails/launches.db` in every build configuration. Startup creates the
component automatically, with WAL, the shared SQLite busy timeout and additive,
versioned schema setup. It starts empty; no environment or Job is imported.

A preset owns an ID, revision, name, description, provider, typed options,
additional argv, optional prompt, enabled state, purpose, timeout and timestamps.
A nonblank prompt derives `agent`; a blank prompt derives `quick-launch`.
Quick Launches can be started manually, but cannot be assigned to automatic
bindings or Board agents. Attachments belong to the preset ID. Presets never
own or copy a CLI installation, home, configuration directory or workspace.

An Action contains exactly one `script`, `vca` or `code-quality` payload. Scripts
use the established containment, interpreter and content-hash validation.
Saving a script Action approves those bytes for the current project, in the
same transaction as its revision. Another project must explicitly save and
approve its own script content. Check findings remain separate from a configured
minimum-grade failure. Check evidence and terminal session references are saved;
terminal transcripts stay in the existing recording store.

## Frontend contract (VIBE-292 / VB-4OG9T-366)

The source contracts are `LaunchContract.cs` and
`VibeRails.Data.Abstractions/Launches/LaunchRecords.cs`. Property names are camelCase
on HTTP. All routes below are under `/api/v1`, on the existing authenticated root
listener. Both credentials are required. The server derives the project; a body
cannot select an arbitrary project. MCP `LaunchTool` uses the same services and
the existing local-user project resolver.

| Route | Contract |
| --- | --- |
| `GET /launch-catalog` | `{items}` with stable ID, definition type, derived kind, revision, capabilities and project eligibility/reason |
| `GET /presets`, `GET /presets/{id}` | Global preset records |
| `POST /presets`, `PUT /presets/{id}` | `SavePresetRequest`; returns `{preset,affectedBindings}` |
| `GET /actions`, `GET /actions/{id}` | Global single Action records |
| `POST /actions`, `PUT /actions/{id}` | `SaveActionRequest`; returns the saved Action |
| `POST /presets/{id}/retire`, `POST /actions/{id}/retire` | `{expectedRevision}`; retains history and attachments |
| `GET /trigger-bindings` | Current-project bindings; optional `definitionId` |
| `POST /trigger-bindings`, `PUT /trigger-bindings/{id}` | `SaveBindingRequest`; `enabled:false` disables |
| `POST /runs` | `{definitionId,definitionType,occurrenceId?}`; queues latest revision |
| `GET /runs` | Optional `definitionId`, `before` and `limit` (1–100); `{runs,nextCursor}` |
| `GET /runs/{id}` | Project-scoped immutable inputs and current process state |
| `POST /runs/{id}/resolve` | `{confirmedStopped:true}` after inspecting an uncertain launch and stopping surviving descendants; rejects live/unverifiable recorded owners, ends as interrupted, never retries |
| `POST /runs/{id}/cancel`, `POST /runs/{id}/retry` | Cooperative cancellation; retry returns a new original-snapshot run |
| `GET /runs/{id}/evidence` | Saved deterministic check evidence |
| `GET, POST /presets/{id}/attachments` | Owner metadata; POST `{name,dataUrl}` |
| `GET /presets/{id}/attachments/{attachmentId}/content` | Owner-scoped download |
| `DELETE /presets/{id}/attachments/{attachmentId}` | Removes from editor, retaining snapshot bytes |
| `GET /runs/{id}/attachments/{attachmentId}/content` | Project- and snapshot-scoped retained attachment |
| `GET, PUT /board/columns/{column}/launches` | `{selections:[{definitionId,definitionType,gate}],revision}` |
| `GET /board/cards/{card}/launches` | Ordered lane entries, process state, verdict and run reference |
| `POST /board/cards/{card}/launches/{entry}/skip` | User skip, scoped to the current entry |

Creates use `expectedRevision:0`; updates must use the positive revision returned
by the server. Definition conflicts return HTTP 409 with
`{error,code:"revision_conflict",currentRevision}`. Validation returns 400 with
`{error,code:"invalid_launch"}`. Missing scoped resources return 404. Board lane
conflicts use the existing `{error}` response. Do not silently resend a stale edit.
Clearing a prompt returns affected schedule/event bindings; these stay visible
and become ineligible. Existing queued Agent snapshots retain their own prompt.

The picker key for a preset is `preset:<stable-id>:<cli>`; `base:<cli>` remains
available. Start saved definitions through `/runs`; observe the run's `tabId`
and `sessionId` to navigate to its terminal. A successful enqueue is not a
successful process. Synchronous Board launches return a conflict and cancel
their queue entry when capacity is unavailable, so manager registration cannot
be deferred past the caller's launch transaction.

Attachment rules reuse Board's filename normalization and byte sniffing: 40
current attachments per owner, the same supported content and no added per-file
size limit. HTTP content is a sandboxed, nosniff download. MCP returns bounded
UTF-8 Markdown/TXT (default 40,000, maximum 250,000 characters), or images up to
5 MiB. Other content stays available in the editor. Removed attachment IDs in
older run snapshots remain readable through the run route. No fake Board card,
relationship or comment is created.

## Execution and recovery

Admission snapshots definition revision, options, composed prompt template,
attachment IDs, trigger occurrence, project and Board context. Editing changes
future admissions only. Retry copies the source snapshot and links `sourceRunId`;
Run now reads the latest definition. Both reject retired/disabled definitions and
unsupported inputs. `{{step:...}}` is rejected on save, admission, retry and
execution, including old queued payloads. Supported ordinary substitutions run
once in the executing child; its exact final prompt is recorded before spawn.

Claims atomically reserve one of three machine-wide slots, with at most one
active run of a definition per project. Root-only scheduling uses a renewable
SQLite lease. Dispatch prefers a root open on the run's project; another root
may take over after 30 seconds only when no root for that project remains open.
Before/after-commit hooks only enqueue; VCA preflight blocking
remains independent. No daemon or OS registration is added.

Run states are `queued`, `claimed`, `running`, `uncertain`, and terminal
`succeeded`, `failed`, `cancelled`, `timed-out`, `interrupted`. Cancellation does
not release running capacity before termination. CLI deadlines are watched by
the owning child; script deadlines use the script runner. Root recovery compares
session outcomes and PID creation times. Scripts also record child identity so
a dead root cannot make a live script appear finished. A crash before durable
spawn acknowledgment remains uncertain and reserves capacity until the user confirms recovery through `/resolve`. Unstarted sealed CLI claims are reconciled safely because spawn must follow the durable start. There is no blind
automatic retry and no exactly-once guarantee for external side effects.

Board assignments and pending entries remain behind `IBoardStore` in `board.db`.
An entry commits a run first, then acknowledges it separately. Repeated delivery
finds the same occurrence even after a definition edit. Ordered entries wait for
successful predecessor processes and, for `result`/`review` gates, an explicit
pass. Review passes require the reporting session's complete, current saved
review. A process exit alone does not approve a review. User skips cancel active
work before releasing successors. Lane configuration edits affect future entries.

## Cutover and compatibility

Environment, environment-settings, Step, Job and old Board Automation HTTP
surfaces return retirement responses. `--env`, `--env-id` and `--job-run` are
retired. Base CLI launches, Board managers and script/check engines remain.
Legacy rows, schema, folders and recordings are retained without conversion or
cleanup. An old saved selection is unavailable until explicitly replaced; names
are never matched to new IDs. An older application version can still run its own
old schedules independently. New capacity claims coordinate new-version roots;
they do not coordinate the retired scheduler in older binaries.

Configuration backups include an online snapshot of `launches.db` and scripts
referenced by current Action bindings. They retain the legacy installation folder
without interpreting retired Environment/Job rows or traversing external retired
homes. Existing state snapshots preserve those old records for recovery.

Provider parity uses `BaseLlmOptionsBuilder` plus process-local controls:
Claude model/effort/YOLO, inline `fastMode` settings, no-session-persistence,
system prompt, bare and debug; Codex model/effort/speed/YOLO and alternate-screen
configuration; OpenCode-family plugin disablement through existing `--pure`
preparation. Additional argv retains other supported flags. Existing command
preparation owns TokenSaver, MCP registration, credentials and sanitization.

## Browser and VS Code integration

The AI Agents view and native VS Code launch tree use the same global catalog.
VS Code preset launches enter the dashboard prompt resolver so custom template values
use the same UI; Actions can be queued directly by the extension.
`POST /runs` optionally accepts `promptPrefix` (for a Quality/Rules task), `initialPrompt`
(filled template values) and `expectedRevision`. The immutable context retains the
one-run override; the saved preset is unchanged. Both prompt fields reject retired step
syntax. Project and execution directory still come only from the authenticated root.

`POST /board/cards/{card}/launches` queues a definition using the saved project-owned
card context and normal Board grants. `GET /board/columns/{column}/launches/activity`
returns current per-card entries with labels and project-scoped run snapshots. The UI
keeps process status and explicit verdict separate and uses the existing retry and
per-card skip endpoints. These additions use normal root authentication and no schema
migration or legacy-data conversion.
