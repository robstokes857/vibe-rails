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
| `["card"]` | The card's linked commits; only their surviving additions at the newest selected tree are scored |
| `["commit", "<SHA>"]` | One commit against its first parent, or the empty tree for an initial commit; 7–40 hexadecimal characters |
| `["working-tree"]` | All changed/untracked files against HEAD, including unrelated card work; committed changes excluded |
| `["range", "<base SHA>", "<head SHA>"]` | Net committed changes; full SHAs required and base must be an ancestor of head |
| `["unpushed"]` | Merge base of upstream and HEAD through HEAD; missing upstream fails visibly |
| `["repository"]` | All committed files at HEAD; dirty/untracked files excluded |

New lane Code quality actions and newly added Code quality steps default to **card** scope.
Existing explicitly configured scopes are retained; edit an existing Automation to select
**Linked commits on the card**. VCA's defaults are unchanged. A card with no linked commits gets
**Needs review**, with “no code attached to this card”; a Research spike is **Skipped/not applicable**.
Other non-code work can explicitly skip the lane check. A standalone run without a card must use
another scope, such as **One commit**. Results never move a card or set its attention flag.

Card capture requires one first-parent history. Divergent or side-branch selections fail visibly
and can be checked individually with commit scope. Each selected commit supplies its changed paths;
the oldest selected commit's first parent and newest selected tree bound capture. Paths follow all
intervening renames before filtering, including chains of unlinked renames. Git line attribution
then removes additions from interleaved, unlinked commits, including additions in the same file.
Reverted/overwritten code is not scored. Full file, rule and impact text is committed context, not
additional code being scored. Root commits use the empty tree; merge commits use their first parent.
The exact resolved commit set is saved in evidence and included in the fingerprint. Comparing a card
check also compares today's linked set; adding/removing a link makes the old evidence stale.
No checkout, staging or worktree mutation is required. Committed captures reuse the existing bounded
blob reader and immutable impact corpus; VCA checks skip that corpus. Working-tree capture is
compared twice (the second pass without impact sources: an edit to one shows up as a changed file)
and rejects a detected edit/HEAD race; this is not a filesystem transaction. Saved Code quality
checks capture the tracked text used for impact ranking too, within the existing text snapshot
budget; sources past the budget are skipped and counted in a limitation, never a failed check.
Quality still analyzes **added code** only, with the user's local ignores and explicit exemptions
captured once for the run. An unreadable ignore list means nothing is ignored, recorded as a limitation.

### File exemptions (VIBE-275)

`exempt_code_quality_file(path, reason, scope="card", card=...)` excludes one exact
repository-relative file. `scope="card"` applies only to checks for that card and commits made
from its linked terminal; it has no wall-clock expiry or lane-triggered deletion.
`scope="repository"` applies across the repository until revoked. Paths cannot be absolute,
directories, globs or parent traversals. A nonblank reason is required. The calling MCP agent's
normal Board attribution (label, CLI and session when known) and creation time are stored.
`list_code_quality_exemptions` lists the effective card and repository entries with IDs;
`revoke_code_quality_exemption` disables one while preserving its reason, author and revocation.
Replacing an entry also preserves the previous version. Reads and writes stay behind `IBoardStore`.

The additive `board-code-quality-exemptions/1` table is separate from the older dashboard ignore
preferences, so older binaries cannot apply a card exemption repository-wide. It is included in
the existing all-table Board backup. No state.db data is changed. Exemption history remains after
a card is deleted; its card-scoped entries no longer apply. These are local settings, not hosted
Board sync content. Shared read-only cards cannot save or revoke them.

Explicit exemptions apply to both `MintLintPreflightStep` and VCA's `Code quality minimum` rule;
they never skip other VCA rules. Legacy dashboard ignores retain their report-only meaning.
Lane checks use the run's explicit project/card, independent of the process's launching session.
Git Guard and `validate_vca` resolve only the inherited session's primary card, after matching
the repository to the card's project or the session's recorded checkout (including sandboxes).
An unrelated repository, another terminal, a branch name or a card key in a commit message does
not supply temporary exemptions. Ordinary unassociated commits use repository exemptions only.
Captured check evidence includes each applied exemption's ID, card scope, reason and author;
revocation does not rewrite past evidence. Freshness comparisons still caution that local
ignore settings and exemptions may have changed. An unavailable exemption store never exempts
files: Git Guard warns and checks all files; a saved check fails visibly if it cannot capture them.

Before the first commit on a branch, working-tree, unpushed and repository checks finish as
**Skipped/not applicable** with “Nothing to check: this branch has no commits yet.” No files
are analyzed, including staged and untracked files; the saved attempt records that limitation
without claiming a captured snapshot. This does not fail the Automation action. A corrupt or
otherwise unavailable HEAD still fails visibly. Explicit commit ranges remain independent of
the current branch and are validated normally.

The action records its attempt before capturing. Findings finish the action successfully and
remain advisory unless a Code quality action sets a minimum grade (below). An analysis failure,
or a missed minimum grade, marks that action/Automation failed but allows subsequent
checks and the review Worker to run. Script/Worker failures remain fail-fast. The ordinary run
cancel/deadline/reaper owns termination. The last Worker's idle-shutdown fallback preserves earlier
failed checks and their error details; pending user cancellation takes precedence. An unfinished
evidence row is resolved against its run when displayed: failed/cancelled/interrupted runs cannot
stay presented as a successful check.
If the run is unavailable, completion is unknown. No background host is introduced.

### Outcome wording

Saved check outcomes distinguish **Passed** (applicable analysis completed without findings),
**Completed with limitations** (analysis ran, but some coverage was skipped, ignored, deferred or
unavailable), **Skipped/not applicable** (nothing applicable was analyzed), **Findings**,
**Failed to run**, and **Cancelled**. Coverage counts and limitations remain part of the evidence;
findings take precedence over coverage limitations. **Needs review** identifies a card check with
no code attached; it remains advisory so the review Worker can attach the intended commits,
unless the action sets a minimum grade.

For check-only Automations, the lane and card Automation rail display the exact run's saved check
outcome and summary through `BoardLaneAutomationStatus.DisplayStatus`, also used by MCP. Multiple
check actions show findings before limitations, and a mix of passed and skipped checks means
completed with limitations. Missing or incomplete evidence is unknown. Retries use their own
evidence; historical attempts are not rewritten. Execution state and `StepStatus` still determine
workflow eligibility: findings and limited/empty coverage remain advisory; a missed minimum grade
fails the run, so the step shows Failed with the grade as its reason. Automations containing
Workers retain the explicit worker verdict; mixed script/check Automations retain the run outcome.

### Minimum grade (VIBE-278)

A Code quality action chooses, when it is added to a lane or edited on the Automations page,
whether findings only post feedback (the default) or **fail the step below grade A, B or C**.
The choice ends the action's argument vector: `["card", "minimum", "C"]`. `JobCheckScope.Parse`
strips it; only Code quality may carry it, and `JobService` rejects it on VCA. No schema changes.
An older binary rejects the longer vector, so the check shows **Failed to run** there: it fails
closed instead of silently passing.

The grade is `CodeQualityRule.EvaluateSnapshot`, the code behind Git Guard's
`Code quality minimum A|B|C` rule: the same health thresholds (A ≥90, B ≥80, C ≥70) over the
overall grade of all added code in scope. Explicit exemptions apply; dashboard ignore preferences
do not, as in the rule. A file the report hides through an ignore still counts toward the grade,
and the limitations say how many files were hidden that way. Below the minimum, the check outcome is **Findings**,
`MinimumGradeMet` is false, `GradeSummary` carries the rule's sentence, and `JobRunner` fails the
action with it as the error. The run then fails, so the lane step fails even when a review Worker
in the same Automation reports a pass; later actions in the Automation (normally that Worker) still
run, as for any failed check. A card with no linked code fails a minimum (nothing could be graded);
a Research spike and any scope with nothing to analyse still skip and pass, as the rule does for an
empty scan. Analysis that fails to run is already a failure and is not graded.

Git Guard enforces the grade only when the user turns on `Code quality minimum X` in VCA, at the
level they choose (WARN/COMMIT/STOP). The lane setting does not change the hook; both share the
grading code, so a step that fails at grade C also fails that rule.

### Results as review reports

Every Code quality check that runs for a card, in any lane and any position, also saves a
canonical review report on that card through `IBoardStore.SaveCheckReviewAsync`, so agents see
it with `get_board_reviews` and in `get_board_card`'s Code reviews. Its id is
`review_check_<check id>`, reviewer **Code quality check**, provider VibeRails. Its result is
Findings, No findings reported, or Incomplete (failed to run, cancelled or no code attached). Its
process status is Failed when the action failed. The findings name the outcome, the minimum grade and the
worst files, and point to `read_board_check`. It has no reviewer session or review run, so it
can never satisfy a reviewer's own pass, and its freshness is the check's: verifying it reports
unknown and points to the card's Checks. It is saved once, with a discussion receipt from VibeRails
that is not sent to Jira or Azure DevOps. A failure to save it is logged and never fails the check.

## Evidence and access

`IBoardStore` owns all writes and reads. Additive component migration `board-checks/1` creates
`BoardChecks` in the normal Board database. Identity includes card, run and action; an attempt is
immutable after completion. Reruns append attempts. Summaries and full engine JSON are stored
separately; history pages contain 50 summaries and the latest result of each engine is queried
independently. Existing state/schema rows are retained and older binaries ignore the new table.

`board-checks/2` adds nullable `EvidenceJson`. New attempts save the full captured text before
analysis: scope files (current/previous text, exact added lines and their source line numbers),
rule files, available impact sources, and the exact ignore rules passed to the engine. Binary or
unavailable files retain manifest entries without invented contents. Evidence is serialized and
sent once, before analysis; the completion save passes none and the upsert keeps the stored column.
SQLite still rewrites the whole row on that update, so the WAL carries the evidence a second time;
avoiding that needs a separate evidence table, which has not been worth the schema change. An impact
source whose text equals a scope file's captured text is stored only in the scope files. Evidence
is read only through `GetCheckEvidenceAsync`, never with a report or summary page: it can run to
tens of megabytes. Failure/cancellation after capture retains the inputs; completed attempts cannot
be overwritten. Older reports have no captured text and remain readable.
The existing Board data backup includes both report and source columns, chunking large values.

Legacy dashboard ignore preferences use `BoardCodeAnalyzerIgnoreStore` over `IBoardStore`. The additive
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
`?verify=true` to compare captured inputs in the current project checkout. The report carries the
record and engine result, not the captured source text. Freshness remains qualified because local ignore preferences may change.
A pinned range whose head differs from current HEAD is stale for the current checkout. A missing
scope/tool/repository is unknown. The snapshot never claims that other scopes were analyzed.

The card report reuses `CodeReportViewer` (Quality Lab and Code Atlas). Saved metrics come from the
record; the graph describes the **current** working tree. Graph failure preserves saved analysis.
Report text is inert, source-generated JSON is used throughout, and requests/viewers are disposed
on close, replacement and navigation. Checks show no aggregate card score.

`get_board_card` includes each engine's latest saved summary; `read_board_check` lists history or
returns one check's record and engine result in 40,000-character chunks. Its `file="*"` lists the
captured scope, rule and impact files, and `file=<path>` reads one file's captured text in the same
chunks. All use the same project/card store boundary.
The new read tool is part of the explicit Board grants on both MCP transports. Review Workers are
instructed to inspect scope, coverage and failures before choosing the next lane through UI/MCP.

## Starter review integration

`ReviewCheckDefaults.Create()` supplies **Code quality (card) → VCA (unpushed)** before the
single review Worker. This avoids an empty working-tree result after a commit. Missing upstream
for VCA must be addressed or the user can select an explicit scope. Actions stay editable,
reorderable and removable. New boards no longer install this special review recipe; existing
recipes and their check actions remain unchanged. VIBE-25 owns
hosted recipe/evidence distribution. Evidence is not added to the hosted Board UI sync contract;
the independent owner-bound Board data backup retains it.

Validation lives in `BoardChecksTests`, Board route/tool tests, existing preflight regressions,
`jobs-controller.test.mjs` and the card Checks cases in `board-ux.spec.js`.
