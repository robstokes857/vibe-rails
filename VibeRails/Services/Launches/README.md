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

## Project scope (VIBE-307)

Agents, Quick Launches and Actions have optional `projectPaths`. An empty array
means **All projects**. New definitions default to that behavior; omission or null
on an update preserves the existing selection. A scope change uses the same
`expectedRevision` as every other definition edit. `ILaunchStore` saves the definition,
scope and any script approval in one transaction, so stale edits cannot partially apply.

Membership uses `LaunchProjectScope.Key`: a full local root path without its trailing
separator, upper-cased on Windows/macOS as with existing launch project keys. Editable
display names and Git remotes never identify projects. Independently opened clones
and worktrees are separate scopes. A Board-launched session retains its owning
project through the existing project resolver, even when working in a managed worktree.
Path aliases/symlinks are not resolved into a shared identity;
use the known root offered by VibeRails. Case-sensitive volumes on Windows/macOS retain
the existing platform-level path-comparison limitation.

`GET /launch-projects` and MCP `list_launch_projects` return known cached roots,
session projects and sandboxes, plus the current project, with `{id,name,path,isCurrent}`.
Closed windows and temporarily absent directories are included; no repository scan
is performed. Names use the same saved display-name reader as Settings. The editor
keeps previously selected IDs even when discovery no longer returns them.

The default catalog contains this project's definitions. `GET /launch-catalog?includeAll=true`
(MCP `includeAll:true`) provides all definitions for management, with `inProject`,
`canLaunch` and an ineligibility reason. Individual definition reads and list routes
remain global. **Manage all projects** on AI Agents deliberately exposes this view.
The terminal/Board preferences catalog retains all entries and their saved order,
annotating `inProject`; launch pickers exclude out-of-project entries even when hidden
items are otherwise included. Saved selections remain visible as unavailable.
Quick actions and the VS Code launch tree use the same project catalog and eligibility.

Scope is enforced for every new admission, including HTTP/MCP, Board, schedule and Git
events. The store rechecks it under the enqueue writer transaction, including retries
of original snapshots. Repeated delivery of an already-admitted occurrence still
returns that run. Narrowing scope does not cancel or rewrite admitted runs or running
processes, bindings, Board selections or history. Existing disable/retire and script
content checks still apply at execution. Retrying a completed run requires current
scope eligibility even though its original execution inputs remain immutable.

All projects means availability, never new schedules, fan-out or script approval.
An unchanged script definition can be managed from outside its scope, or when its
file is unavailable here, without approving its bytes in that project; changed script payloads still require normal
validation. A newly allowed project must explicitly approve its own script content.

`launches/4` automatically creates the additive `DefinitionProjects` mapping table;
zero mappings means global. Reads overlay it on the definition JSON. Older writers
that omit unknown JSON fields cannot erase mappings. Older executables **do not enforce
project scope**; additive storage compatibility cannot change their launch behavior.
No schema generation bump, backfill, data conversion or manual migration is required.

## Frontend contract (VIBE-292 / VB-4OG9T-366)

Code quality Actions are offered in every UI catalog, Quick actions, Board add/run choices
and the definition editor. VIBE-309 briefly hid them with a presentation filter; VIBE-311
removed it. No stored data was changed by either.

The source contracts are `LaunchContract.cs` and
`VibeRails.Data.Abstractions/Launches/LaunchRecords.cs`. Property names are camelCase
on HTTP. All routes below are under `/api/v1`, on the existing authenticated root
listener. Both credentials are required. The server derives the project; a body
cannot select an arbitrary project. MCP `LaunchTool` uses the same services and
the existing local-user project resolver.

| Route | Contract |
| --- | --- |
| `GET /launch-catalog` | `{items}` with stable ID, definition type, derived kind, revision, capabilities and project eligibility/reason; `includeAll=true` for management |
| `GET /launch-projects` | Known local project IDs, saved display names and paths, including the current project |
| `GET /presets`, `GET /presets/{id}` | Global preset records |
| `POST /presets`, `PUT /presets/{id}` | `SavePresetRequest`; returns `{preset,affectedBindings}` |
| `GET /actions`, `GET /actions/{id}` | Global single Action records |
| `POST /actions`, `PUT /actions/{id}` | `SaveActionRequest`; returns the saved Action |
| `POST /presets/{id}/retire`, `POST /actions/{id}/retire` | `{expectedRevision}`; retains history and attachments |
| `GET /trigger-bindings` | Current-project bindings; optional `definitionId` |
| `POST /trigger-bindings`, `PUT /trigger-bindings/{id}` | `SaveBindingRequest`; `enabled:false` disables |
| `POST /runs` | `{definitionId,definitionType,occurrenceId?,nativeTerminal?}`; queues latest revision; presets always use native terminals outside Board; Actions retain the requested presentation (default false) |
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
| `GET /board/cards/{card}/launches/runs` | `{runs,nextCursor:null}`: that card's runs newest first, 50 per page; `offset` (0–1,000,000) pages back. The card dialog uses it instead of filtering project-wide `/runs` |
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
successful process. Native runs have no tab ID. All non-Board preset launches, including
the catalog, Quick actions, terminal picker, VS Code, Quality/Rules fixes, schedules and
Git events, use native terminals. UI callers leave the current view open and poll for
startup or failure. `LaunchService` enforces the destination even for callers that request
`nativeTerminal:false`. Board managers, card runs (including the manual trigger) and lane
agents use in-app terminals, even if a caller requests native presentation. Scripts and
checks retain their existing presentation; signed library scripts use their private window.
Synchronous Board launches return a conflict and cancel
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

For a native run, the root claims capacity and uses the existing OS-terminal launcher
to invoke `--launch-run <id> --workdir <dir>` as separate argv. That execution child
starts no root scheduler. It validates the immutable snapshot again and owns normal
session recording, cancellation and deadlines. Presets use the native console/PTY
path; prompt-bearing Agents receive explicit instructions to save their results and call
`end_agent_session` after completing their assignment. `NativeLaunchRunner` adds this guidance
before placeholder resolution and recording the final prompt, preserving the snapshot's
composed prompt/override precedence. Native Quick Launches receive no auto-close instruction.
Actions stream their captured output to the console and the recording. No
legacy Job or Environment is created. Native mode is an additive snapshot JSON
field, with no schema change. A native claim unacknowledged after 30 seconds is
atomically sealed only if execution has not started; a late child then cannot spawn.
Run history retains cancellation and outcomes even when the native window closes.

The opt-in Windows fixture at `Tests/headless/NativeLaunchSmoke` exercises real
native windows, preset PTYs, script execution, output, cancellation and process exit
using disposable data and fixed PowerShell commands; see its README.

Admission snapshots definition revision, options, composed prompt template,
attachment IDs, trigger occurrence, project and Board context. Editing changes
future admissions only. Retry copies the source execution inputs and links `sourceRunId`,
applying the current Board/native presentation policy to the new preset run without
changing the original record. The store applies that policy after loading the authoritative
retry source. Run now reads the latest definition. Both reject retired/disabled definitions and
unsupported inputs. `{{step:...}}` is rejected on save, admission, retry and
execution, including old queued payloads. Supported ordinary substitutions run
once in the executing child; its exact final prompt is recorded before spawn.

Claims atomically reserve one of three machine-wide slots, with at most one
active run of a definition per project. Root-only scheduling uses a renewable
SQLite lease. Dispatch prefers a root open on the run's project; another root
may take over after 30 seconds only when no root for that project remains open.
Before/after-commit hooks only enqueue; VCA preflight blocking
remains independent. No daemon or OS registration is added.

Hooks never open `board.db` (`LaunchEventService.Create` builds a `LaunchService`
without a Board store), so a busy, newer or damaged Board database cannot fail a
commit. Their preset snapshots leave `ContextIncluded` off; the root's terminal
start then adds the nudge and App/Board context for the run's project, as the
retired Jobs hook path did. Every other admission composes them up front.

`ProjectKey` (upper-cased on Windows and macOS) is only the comparison and lease
key in `Runs.ProjectPath`, bindings and `project:<key>` leases. It is never a
working directory or a path shown to the user. Each snapshot context records
`projectRoot`, the project as the queuing root spelled it, and `Root` falls back
to the key for runs queued before VIBE-296. Bindings record their root the same
way. Nested Board launches, Action scripts and checks, Board links and context
resolution use it. `launches/3` adds `ProjectRootPaths(ProjectKey, ProjectRoot)`,
which the scheduler writes alongside its project lease. Open-root readers, such as
the review inbox clone list, return those paths. A lease from an older build still
answers with its key. The table is separate, not a `RuntimeLeases` column, because
older builds insert into that table positionally.

A Board launch passes `sessionTitle` (`{DisplayId} · {Title}`). The dispatcher
uses it for the tab title and for the card's one session link. When the CLI's MCP
connection attached the session first, the dispatcher refines that attachment's
origin instead of linking it again. Agent comments take their purpose from a saved
review or review origin first, then from the preset snapshotted on the session's
launch run (`BoardService.FindAgentPurposeAsync`). Lane entries are written only
when their process status changes, and entries whose run has ended are not ticked.

Run states are `queued`, `claimed`, `running`, `uncertain`, and terminal
`succeeded`, `failed`, `cancelled`, `timed-out`, `interrupted`. Cancellation does
not release running capacity before termination. An Action cancelled between claim
and durable start acknowledges its owned claim immediately, releasing unused capacity
without creating or finalizing a session. Dispatch validation failures and request
cancellation before handoff also release the owned claim, using cleanup independent
of the cancelled request. Once handed off, the execution owner retains responsibility
for termination and capacity release. CLI deadlines are watched by
the owning child; script deadlines use the script runner. Prompt-bearing Agent presets
also finish after two minutes without PTY output, for native and in-app runs. This closes
the specific CLI process tree, finalizes its recording and removes its agent tab; Quick
Launches and ordinary interactive terminals remain open. Every nonempty PTY chunk restarts
the idle window. Inactivity is recorded distinctly from an explicit `end_agent_session`
call, and a successful idle close never supplies a missing review/check verdict.
Root recovery compares
session outcomes and PID creation times. Scripts also record child identity so
a dead root cannot make a live script appear finished. A crash before durable
spawn acknowledgment remains uncertain and reserves capacity until the user confirms recovery through `/resolve`. Unstarted sealed CLI claims are reconciled safely because spawn must follow the durable start. There is no blind
automatic retry and no exactly-once guarantee for external side effects.

Board assignments and pending entries remain behind `IBoardStore` in `board.db`.
An entry commits a run first, then acknowledges it separately. Repeated delivery
finds the same occurrence even after a definition edit. Ordered entries wait for
predecessor processes to end, then continue after success, failure, cancellation,
timeout or interruption, regardless of the reported result. Queued, claimed,
running and uncertain processes still hold their successors. The lane editor
offers ordering only, with no continuation-result selector. Existing `gate`
fields remain compatible storage/wire metadata and preserve legacy review
attribution and validation, but never decide whether the next entry runs. New
UI selections omit the field; no stored selections or history are converted.
Review passes still require the reporting session's complete, current saved
review when review validation applies. A process exit alone does not approve a
review. User skips cancel active work before releasing successors. Lane
configuration edits affect future entries.

## Cutover and compatibility

Environment, environment-settings, Step, Job and old Board Automation HTTP
surfaces return retirement responses. `--env-id`, `--job-run` and `--env` with a
custom environment name are retired: `Program.cs` exits with code 2 through
`RetiredLaunchArguments.IsRetired`. `vb --env <base-cli> --workdir <dir>` remains,
because native-terminal base CLI launches (the sandbox launch route) spawn exactly
that. Base CLI launches, Board managers and script/check engines remain.
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

The catalog returns retired definitions (`retired: true`, never launchable) so saved
selections and history can still name them. Every launch list hides them: the AI Agents
page, Quick actions (through `canLaunch`) and the VS Code launch tree. A list that shows
them presents deleted definitions as "Unavailable" (VIBE-311).

The signed script library is not part of this catalog or of `launches.db`. Scripts
registered there (VS Code's **Add to VibeRails Scripts…**) are listed beside the catalog by
the AI Agents page, and their signed entries by Quick actions and the VS Code launch tree,
from `GET /api/v1/python-scripts`. They run in the library's private script terminal with
its own signing rules, never through `/runs`; see the
[script library guide](../PythonScripts/AGENTS.md). A script Action is the definition to
use for run history, project scope, schedules or Board lanes.
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
