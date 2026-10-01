# Card checks

Every saved card exposes Checks without starting analysis on save. **Run checks** chooses an
existing project Automation; its ordered actions, Worker, workspace and run recording stay under
the ordinary per-card Automation machinery. Actions 2 (`CodeQuality`) and 3 (`Vca`) call
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
detected edit/HEAD race; this is not a filesystem transaction. Working-tree impact ranking reads
live tracked files. Quality still analyzes **added code** only, with the user's local ignores.

The action records its attempt before capturing. Findings finish the action successfully and
remain advisory. An analysis failure marks that action/Automation failed but allows subsequent
checks and the review Worker to run. Script/Worker failures remain fail-fast. The ordinary run
cancel/deadline/reaper owns termination. An unfinished evidence row is resolved against its run
when displayed: failed/cancelled/interrupted runs cannot stay presented as a successful check.
If the run is unavailable, completion is unknown. No background host is introduced.

## Evidence and access

`IBoardStore` owns all writes and reads. Additive component migration `board-checks/1` creates
`BoardChecks` in the normal Board database. Identity includes card, run and action; an attempt is
immutable after completion. Reruns append attempts. Summaries and full engine JSON are stored
separately; history pages contain 50 summaries and the latest result of each engine is queried
independently. Existing state/schema rows are retained and older binaries ignore the new table.

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
reorderable and removable. VIBE-23 owns consuming this factory in new-board template creation and
recoverable cross-database seeding; existing boards/workflows are not retrofitted. VIBE-25 owns
hosted recipe/evidence distribution. Local evidence currently remains local and is not added to
the Board sync wire contract by this change.

Validation lives in `BoardChecksTests`, Board route/tool tests, existing preflight regressions,
`jobs-controller.test.mjs` and the card Checks cases in `board-ux.spec.js`.
