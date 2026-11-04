# Card checks

Every saved card exposes the latest Checks summary inside its collapsed Advanced section,
without starting analysis on save. VIBE-34
removes Run checks, Earlier results and the Code reviews panel from the card. Evidence and APIs
remain intact. The lane **Agents** dropdown can create a VCA or Code quality Automation with an
explicit scope and select it for future lane entry; setup never starts a run. Existing multi-action
Automations remain editable on the Automations page. Actions 2 (`CodeQuality`) and 3 (`Vca`) call
`MintLintPreflightStep` and `VcaPreflightStep` directly. They never call the Git preflight pipeline,
enqueue VCA-rule triggers, or move a card. Existing Git Guard behavior stays in that pipeline.

## Scope and execution

Check actions store their validated scope vector in the existing `ArgumentsJson` snapshot:

| Arguments | Captured inputs |
| --- | --- |
| `["working-tree"]` | All changed/untracked files against HEAD, including unrelated card work; committed changes excluded |
| `["range", "<base SHA>", "<head SHA>"]` | Net committed changes; full SHAs required and base must be an ancestor of head |
| `["unpushed"]` | Merge base of upstream and HEAD through HEAD; missing upstream fails visibly |
| `["repository"]` | All committed files at HEAD; dirty/untracked files excluded |

For one commit, select its parent as base and the commit as head. For an initial commit at HEAD,
repository scope covers that committed tree. Linked commits never choose or imply a range. No
checkout, staging or worktree mutation is required. Committed captures reuse the existing bounded
blob reader and immutable impact corpus. Working-tree capture is compared twice and rejects a
detected edit/HEAD race; this is not a filesystem transaction. Saved Code quality checks capture
the tracked text used for impact ranking too, within the existing text snapshot budget. Quality
still analyzes **added code** only, with the user's local ignores captured once for the run.

The action records its attempt before capturing. Findings finish the action successfully and
remain advisory. An analysis failure marks that action/Automation failed but allows subsequent
checks and the review Worker to run. Script/Worker failures remain fail-fast. The ordinary run
cancel/deadline/reaper owns termination. The last Worker's idle-shutdown fallback preserves earlier
failed checks and their error details; pending user cancellation takes precedence. An unfinished
evidence row is resolved against its run when displayed: failed/cancelled/interrupted runs cannot
stay presented as a successful check.
If the run is unavailable, completion is unknown. No background host is introduced.

## Evidence and access

`IBoardStore` owns all writes and reads. Additive component migration `board-checks/1` creates
`BoardChecks` in the normal Board database. Identity includes card, run and action; an attempt is
immutable after completion. Reruns append attempts. Summaries and full engine JSON are stored
separately; history pages contain 50 summaries and the latest result of each engine is queried
independently. Existing state/schema rows are retained and older binaries ignore the new table.

`board-checks/2` adds nullable `EvidenceJson`. New attempts save the full captured text before
analysis: scope files (current/previous text, exact added lines and their source line numbers),
rule files, available impact sources, and the exact ignore rules passed to the engine. Binary or
unavailable files retain manifest entries without invented contents. Evidence is loaded only with
the full report, never in summary pages. Failure/cancellation after capture retains the inputs;
completed attempts cannot be overwritten. Older reports have no captured text and remain readable.
The existing Board data backup includes both report and source columns, chunking large values.

Current exemptions use `BoardCodeAnalyzerIgnoreStore` over `IBoardStore`. The additive
`board-code-quality-ignores/1` tables retain current values and every saved/removal event, including
path, match kind, reason, original creation time and author. New author values identify the local
OS account, not a verified cloud identity. Legacy `state.db` rules have unknown authors. Reads and
backups retain legacy observations without changing the old table. Untouched adopted rules follow
old-version edits/removals; a choice made by the current version wins over conflicting old-version
writes, which are still archived. Removal retains the original exemption and its history.

Records include start/end times, engine version, actual scope file manifest, checkout, base/head,
SHA-256 input and rule fingerprints, coverage/finding counts and explicit limitations. VCA keeps
WARN/COMMIT/STOP and deferred findings. No applicable rules, unsupported/ignored files, missing
sources and deferred analysis cannot become an unqualified green result. Counts for VCA refer to
rules; Code quality counts files, with findings counting files at NeedsWork/AtRisk.

`GET /api/v1/board/cards/{card}/checks` reads summaries, current waiting/running actions and eligible
Automations. `GET .../checks/{id}` loads full evidence immediately; **Compare inputs** adds
`?verify=true` to compare captured inputs in the current project checkout. Freshness remains qualified because local ignore preferences may change.
A pinned range whose head differs from current HEAD is stale for the current checkout. A missing
scope/tool/repository is unknown. The snapshot never claims that other scopes were analyzed.

The card report reuses `CodeReportViewer` (Quality Lab and Code Atlas). Saved metrics come from the
record; the graph describes the **current** working tree. Graph failure preserves saved analysis.
Report text is inert, source-generated JSON is used throughout, and requests/viewers are disposed
on close, replacement and navigation. Checks show no aggregate card score.

`get_board_card` includes each engine's latest saved summary; `read_board_check` lists history or
returns full evidence in 40,000-character chunks. Both use the same project/card store boundary.
The new read tool is part of the explicit Board grants on both MCP transports. Review Workers are
instructed to inspect scope, coverage and failures before choosing the next lane through UI/MCP.

## Starter review integration

`ReviewCheckDefaults.Create()` supplies **Code quality → VCA**, both in unpushed scope, before the
single review Worker. This avoids an empty working-tree result after a commit. Missing upstream
must be addressed or the user can select an explicit range/repository scope. Actions stay editable,
reorderable and removable. New boards no longer install this special review recipe; existing
recipes and their check actions remain unchanged. VIBE-25 owns
hosted recipe/evidence distribution. Evidence is not added to the hosted Board UI sync contract;
the independent owner-bound Board data backup retains it.

Validation lives in `BoardChecksTests`, Board route/tool tests, existing preflight regressions,
`jobs-controller.test.mjs` and the card Checks cases in `board-ux.spec.js`.
