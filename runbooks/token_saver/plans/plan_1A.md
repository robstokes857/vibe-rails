# TokenSaver plan 1A — the first enhancement set

**Status: IMPLEMENTED in the working tree (2026-08-16, later the same day) — not yet
committed or shipped; the live soak is owed.** Rob decides what ships.
Living document: update the per-item Status lines as things land or die, and add a line to
`../mining_runbook.md` §8 when the plan's state changes materially.

## Where we left off (2026-08-16, session end) — pick-up notes

A1+A2+A3 are coded, tested, and documented, sitting **uncommitted** in a working tree that
also carries Rob's unrelated in-flight changes — stage explicit paths only. Nothing has run
against live traffic yet.

**The plan_1A footprint (7 files, out of a busier tree):**

- `TokenSaver/Pipeline/CompressionCatalog.cs` — `"exec"` in scope-shell CodexTools, `"wait"`
  in scope-shell-background. The whole product change for A1+A2.
- `TokenSaver/Minify/CodexResponsesRewriter.cs` — `DefaultToolAllowlist` synced (backs the
  legacy overload only; the live path resolves from the catalog).
- `VibeRails/DTOs/ResponseRecords.cs` — `CompressionPreviewRequest` widened: nullable
  `CaptureId` plus `Text`/`ToolName`/`Command`/`Provider` (additive — the captures UI's
  `{captureId, enabledIds}` payload binds unchanged).
- `VibeRails/Routes/CompressionCaptureRoutes.cs` — handler extracted as `PreviewAsync`
  (TypedResults; exactly-one-form validation; missing text-form fields → 400).
- `Tests/TokenSaver/CodexResponsesRewriterTests.cs` — fixture pin flipped
  (`…CompressesTextAndLeavesBinaryBlocksByteVerbatim`), multi-block and mixed tests updated.
- `Tests/Routes/CompressionCaptureRoutesTests.cs` — six new `Preview_*` tests +
  `StubCaptureStore`.
- `TokenSaver/README.md` — scope table now names all three providers' tools + exec/wait note.

(Plus this file and `../mining_runbook.md` §1/§2b/§4/§5/§7/§8.)

**Test state at hand-off:**

- `dotnet test Tests/Tests.csproj --filter
  "FullyQualifiedName~TokenSaver|FullyQualifiedName~Compression|FullyQualifiedName~LlmProxy"`
  → **549/549 green**.
- Building while the dev instance runs fails on the locked `VibeRails/bin/.../vb.exe`; add
  `-p:OutputPath=<isolated-dir>/` to redirect the whole build graph (delete the dir after).
- The full `Tests.csproj` run showed 2 failures **outside** this plan's area — attributed to
  Rob's concurrent edits (AgentFileService / CliSpawnCommandBuilder neighborhood), not
  investigated at his instruction. If they persist, they predate plan_1A.

**Measurement bonus found while flipping the fixture test:** the real minifier compresses
ANSI-bearing output that the Python mirror declines. Under curated defaults, an exec output
of `ESC[31m…ESC[0m` + two trailing spaces + CRLF keeps the SGR bytes (ansi-strip off) while
trailing-whitespace, crlf-normalize and blank-edges still fire — confirmed against the real
pipeline. The mirror refuses anything containing ESC, so **A1's 646.7 MB wire figure is a
floor**; real exec savings should land above it.

**Next steps, in order:**

1. Rob commits (explicit paths) and deploys. Sessions recycle on deploy — no cache concern
   (owner ruling, `../mining_runbook.md` §5).
2. Soak (A1 validation steps 2–3): `TokenSaverCaptureEnabled: true` → drive a real code-mode
   Codex session → judge a handful of fresh `exec` capture GUIDs via
   [`../compress_runbook.md`](../compress_runbook.md), watching specifically for wire-guard
   rejections (`RewriteAccepted=0 ∧ Changed=1`) on exec.
3. Re-measure: `python build_corpus.py --name <you>` (incremental) + `corpus_stats.py` over
   post-deploy exchanges — openai passthrough should fall visibly from 94%. The savings tally
   proves bytes came off; only fresh exchanges prove exec did it.
4. Update the Status lines here (implemented → validated/shipped) and add a
   `../mining_runbook.md` §8 line.

**Where these numbers come from.** The mining corpus (all 20,733 proxied exchanges,
2026-07-29 → 2026-08-16; 20,271 unique tool outputs; 2,933 MB wire-weighted chars) plus
harness runs with `candidates/curated_stages_mirror.py` — a *Python approximation* of the
curated ON stages (conservative fail-open: declines on any ESC/BEL/bare-CR, so lossless
savings are under-counted, never over-counted). Reports:
`python-scripts/token_saver/results/claude-plan/`. To regenerate on fresh traffic:

```
python build_corpus.py --name <you>          # incremental — only new exchanges
python experiment.py candidates/curated_stages_mirror.py --name <you> --provider openai --tool exec --on raw
```

"Wire MB" = chars × resend count (what actually crosses the wire); "display tokens" =
chars/4, matching the product meter. All estimates need §7 real-pipeline validation before
anyone believes the last digit.

---

## A1 — Allowlist Codex code-mode `exec` output  ← the centerpiece

**Evidence.** `exec` is 61% of every tool-output char the proxy has ever carried
(11,622 unique outputs, 1,787 MB wire) and it is not allowlisted, which is the main reason
94% of real Codex `/responses` requests pass through byte-identical today. The curated-stage
mirror over all of it: fired on 9,454 outputs, **saved 16.8 MB unique / 646.7 MB wire
(~162 M display tokens over the 18-day window, ≈36% of exec's wire)** — zero exceptions,
zero invariant violations, zero wire-guard rejections. 9,050 of 9,454 rewrites were pure
lossless (whitespace/CRLF); the 404 lossy ones are `dedupe-lines`/`truncate-long` markers on
giant outputs and carry much of the tonnage.

**Change.** Add `"exec"` to the `scope-shell` CodexTools list in
`TokenSaver/Pipeline/CompressionCatalog.cs`. That is the whole product change: no new
stage, no new setting, no new scope id — the per-LLM switch and the curated set stay the
only user surface, and the wire-format ids are untouched.

**Why it composes cleanly.**
- The Codex rewriter already extracts `exec` text blocks today (diagnostic-only capture
  path, 637ee1b) and already skips the `input_image`/`input_audio` blocks that code-mode
  output mixes in. Allowlisting flips those strings from "captured unchanged" to
  "pipeline-compressed"; binary blocks stay untouched.
- `exec`'s command is JavaScript source → `CommandShapes.Classify` returns None
  (metachars) → shape stages 7–10 skip naturally. Only minify + condense run — exactly
  what the mirror measured.

**Risk, honestly stated.** Code-mode JS can surface file contents in its output; if Codex
later edits based on those exact bytes, a rewrite could break the match. This is the same
risk profile as `cat` through the already-allowlisted `shell_command` — precedent accepted —
but it deserves a capture-audited soak (below), not a shrug.

**Validation (runbook §7).**
1. (**done 2026-08-16**) Tests first: the code-mode fixture test used to *assert* exec was
   NOT in the allowlist — now flipped and extended over `code_mode_exec_request.json`
   (`Rewrite_CurrentCodeModeExec_CompressesTextAndLeavesBinaryBlocksByteVerbatim`);
   `CuratedDefaults_*` pins checked (unaffected — they pin ids, not tool lists).
2. (**owed**) `TokenSaverCaptureEnabled` on + real code-mode traffic → judge a handful of
   capture GUIDs via `compress_runbook.md`; `TokenSaverStageOverride` is the live bisect hatch.
3. (**owed**) Watch the savings tally and fresh exchanges for a day: passthrough rate on
   `/responses` should fall visibly from 94%.

**Status: implemented 2026-08-16** — `"exec"` in scope-shell CodexTools; the fixture test
flipped (`Rewrite_CurrentCodeModeExec_CompressesTextAndLeavesBinaryBlocksByteVerbatim`),
mixed/multi-block tests updated, `CuratedDefaults_*` pins unaffected (they pin ids, not tool
lists). Owed: validation steps 2–3 (capture-audited soak + passthrough re-measure) after
deploy.

## A2 — Allowlist Codex `wait` output (ride-along)

**Evidence.** 434 unique outputs, 33.5 MB wire; mirror saves 3.6 MB wire (~0.9 M display
tokens). Small, but the mechanism is identical to A1.

**Change.** Add `"wait"` to `scope-shell-background` CodexTools (it is background-job
output — the moral equivalent of Anthropic's `BashOutput` / Codex's `write_stdin`).

**Why now:** identical mechanism to A1, so it rides the same change. (The original batching
rationale — one prompt-cache bust — was retired by owner ruling 2026-08-16: single user,
deploys recycle sessions; see runbook §5.) **Status: implemented 2026-08-16,** same owed
soak as A1.

## A3 — Text-accepting compression preview (enabler, not savings)

`POST /api/v1/compression/preview` only replays stored capture GUIDs, so exchange-mined
candidate texts have nothing to preview against without reproducing live traffic. Add an
optional body shape `{text, toolName, command, provider, enabledIds?}` next to the existing
`{captureId}` form (same handler, same pipeline, same auth). This closes the loop between
the Python harness and the real pipeline: §7 validation of any future candidate becomes one
curl instead of a traffic-reproduction session. **Status: implemented 2026-08-16** —
`{text, toolName, provider, command?, enabledIds?}` beside `{captureId}` (exactly one form
per request; both/neither/missing-fields → 400), handler extracted as
`CompressionCaptureRoutes.PreviewAsync` with route-level tests.

---

## Parked — measured, deliberately not in 1A

| idea | number | why parked |
|---|---|---|
| `Read`-family scopes (anthropic `Read` 449 MB wire, zai `read` 218 MB; mirror saves 126.6 + 84.5 MB) | 2nd-biggest pool | **Owner decision stands** (scope-read OFF: failed-Edit risk). Extra hazard the mirror exposed: `truncate-long` on a Read output elides exactly the middle a later Edit needs. If ever revisited: minify-only stage subset + a capture-based failed-Edit study first. |
| Metachar sub-recognizer (267 MB wire declined for shape stages) | pool is real, slice isn't | The obvious cheap carve-out (trailing `2>&1`) measures **1.8 MB / 15 outputs** — worthless. Anything more means real parser design for piped commands. Revisit only with a specific shape + numbers. |
| `git-diff` shape filter (v1 no-op) | 16.8 MB wire | Small; needs a whole new filter design. |

## Closed — negative results on record (don't re-litigate without new data)

- `dedupe-lines` threshold 3→2: fires on 60 outputs, ~22.5 k display tokens. Not worth a
  stage change (`results/claude-full/dedupe_runs_of_two_*.md`).
- Download/progress-spam collapser: fires **zero** times on real traffic.
- `Grep` scope for minify+condense: ~525 display tokens across 649 outputs — grep output
  is already dense. (Shape-grouping via `scope-grep` remains a separate, owner-parked axis.)

## Shipping notes (when Rob green-lights)

- A1+A2+A3 landed as one batch (the cache-bust half of that rationale is retired — owner
  ruling 2026-08-16, runbook §5): README scope table, runbook §1/§2b/§4/§5/§7/§8, and
  these statuses were all updated together.
- Invariants are non-negotiable (runbook §5); the C# pipeline, not the Python mirror, is
  the acceptance gate. Worst outcome of any bug must stay "a request that saves nothing."
