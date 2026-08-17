# Mining runbook — finding the next token savings

**Purpose.** This is the jump-off for mining the data TokenSaver has already captured to find
ways to save more tokens. You are hunting for the next stage, the next allowlist entry, the next
recognized command shape — with measured evidence instead of vibes. The capture infrastructure
was built for exactly this: raw exchanges are the input to every future what-if, including
stages that do not exist yet.

**This is a living document, and so are its scripts.** If a section, query, script, or rule here
stops earning its place, change it — and add a line to §8 History. Improving the scripts is part
of doing the job, not scope creep. (Precedent: Rob deleted most of the first compress runbook
because it stopped earning its place.)

**The one-line invocation:**

> Read `runbooks/token_saver/mining_runbook.md` and go find me new token savings.

Everything below is written for the fresh agent that gets that message.

---

## 0. Required background

Read these first. You cannot judge a savings idea without them.

1. [`TokenSaver/README.md`](../../TokenSaver/README.md) — the whole system and **the invariants**
   (deterministic, idempotent, never-grows, fail-open). The worst outcome of any TokenSaver bug
   must remain *a request that saves nothing*. No amount of mined savings justifies bending that.
2. [`compress_runbook.md`](compress_runbook.md) (sibling) — judging one capture by GUID.
3. `TokenSaver/Pipeline/CompressionCatalog.cs` — the single source of truth for stages and
   scopes. **Ids are a wire format** (persisted in settings.json and in every capture row);
   renaming one silently re-reads history as "stage disabled". Never rename.

## 1. What the saver does today (snapshot 2026-08-16 — re-derive from the catalog if in doubt)

Only tool-output strings of allowlisted tools are ever rewritten; nothing else in a request body
is touched, and responses are never touched. Per string:
minify (stages 1–6, fused) → shape filters (7–10, keyed off `CommandShapes.Classify(command)`,
never output sniffing) → condense (11–12, fused).

| # | id | kind | default | notes |
|---|----|------|---------|-------|
| 1 | `cr-collapse` | Lossless | **OFF** | owner preference: don't rewrite terminal-shaped output |
| 2 | `crlf-normalize` | Lossless | ON | split out so Windows payloads compress at all |
| 3 | `ansi-strip` | Lossless | **OFF** | owner preference, same reason as cr-collapse |
| 4 | `trailing-whitespace` | Lossless | ON | |
| 5 | `blank-edges` | Lossless | ON | |
| 6 | `blank-runs` | Lossless | ON | |
| 7 | `git-status-group` | Reshaping | ON | porcelain `git status` only |
| 8 | `grep-group` | Reshaping | ON | requires explicit `-n`/`--line-number` |
| 9 | `find-group` | Reshaping | ON | requires a recognized GNU predicate |
| 10 | `elide-passed-tests` | Lossy | ON | recognized test runners; `[... N passed ...]` |
| 11 | `dedupe-lines` | Lossy | ON | runs of ≥3 identical lines → ` [xN]` |
| 12 | `truncate-long` | Lossy | ON | head 150 + tail 50 when middle ≥10 lines and ≥4 KiB |

Scopes (which tools feed the pipeline), per provider:

| scope | default | Anthropic | Codex | zai + xai |
|-------|---------|-----------|-------|-----------|
| `scope-shell` | ON | Bash, PowerShell | shell_command, exec_command, exec | bash |
| `scope-shell-background` | ON | BashOutput | write_stdin, wait | — |
| `scope-read` | **OFF** | Read | — | read |
| `scope-grep` | **OFF** | Grep | — | grep |

`CommandShape` gatekeeps stages 7–10: the whole command declines on ANY shell metacharacter
(pipe, redirect, `&`, `;`, backtick, `$`, parens — even inside quotes), and the first token must
literally be the recognized command (sudo/env/absolute-path/docker wrappers decline). `GitLog`,
`GitDiff`, `DirectoryListing` classify today but have **no filter** — deliberate v1 no-ops.
Shape filters and the condenser fail open on ESC/BEL/CR anywhere in the string.

## 2. The two datasets

### 2a. `ProxyExchanges` — every request/response, always on

`~/.vibe_rails/proxy_exchanges.db` (its own file beside state.db, never in it). WAL,
busy_timeout 5000, written by whichever tab-child `vb.exe` hosts the proxy; external read-only
browsers are expected by design. Logging is unconditional for authenticated traffic on all four
proxy routes — there is deliberately no setting that disables it. No retention: deleting the
file is the only pruning.

| column | meaning / gotcha |
|--------|------------------|
| `Id` | GUID, PK |
| `CreatedUTC` | **enqueue** time, ISO-8601 `"o"`; DESC index — the only index |
| `Provider` | `openai` \| `anthropic` \| `zai` \| `xai` — stable key, set per route |
| `Method`, `Path` | path only; query string deliberately excluded |
| `StatusCode` | upstream status; `0` = relay failed before response headers |
| `RequestBefore` / `RequestAfter` | full body either side of the rewrite. Equal ⇒ passthrough — **that equality is the mining signal this schema was designed around**. Never truncated, EXCEPT: bodies over the transform's 10 MB buffering cap are streamed untouched and logged with **both fields empty** — filter those degenerate rows |
| `ResponseBody` | capped at 32 MB (`ResponseTruncated=1`); the client always got the full response |
| `CharsBefore/After`, `ResponseChars` | UTF-16 **char** counts of the decoded bodies, not bytes |
| `ElapsedMs` | recorder construction → build |

Scale (2026-08-16): 20,750 rows, 18.4 GB, 2026-07-29 onward; provider mix: openai 12,829,
anthropic 5,488, zai 2,457. Growth ≈ 1 GB/day.

**The superlinear-resend trap.** The CLI resends the whole conversation every turn, so
consecutive exchanges in a session are near-duplicates, each larger than the last. Any naive
aggregate counts the same tool output hundreds of times. Always compute both views:
**unique chars** (dedupe by content hash — honest "how much distinct text exists") and
**wire chars** (× resend count — honest "what it costs"). A byte saved in an output resent 40
times is worth 40 bytes of wire; that multiplier is also why mining should weight outputs that
appear *early* in long sessions.

### 2b. `CompressionCaptures` — per-string verdicts, opt-in

`state.db`, gated by `TokenSaverCaptureEnabled` (default false). Grain: **one row per textual
tool-output string the pipeline considered** (each text block of an array-form result is its own
row). Self-deduping: `ContentHash` (versioned SHA-256 over provider+tool+command+ids+texts+
disposition) with a UNIQUE partial index; a repeat only bumps `SeenCount` — so **the resend
multiplier is already computed for you here**. Uncapped by explicit decision;
`DELETE /api/v1/compression/captures` is the only (collection-wide) reset.

Rows worth mining:

- `RewriteAccepted=0` **and** `Changed=1` → the wire-size guard rejected a candidate whose
  re-serialized token was ≥ the original (escaping). Quantifies recoverable "missed" savings.
- Codex diagnostic-only rows: tools outside the Codex allowlist (including code-mode `exec`,
  whose output mixes text with binary blocks) are captured unchanged — `RawText==CompressedText`,
  `RewriteAccepted=0`, trace `[]`. These are your allowlist-expansion candidates, pre-collected.
- **Blind spot:** only the Codex rewriter has that diagnostic pass. Anthropic/zai/xai
  non-allowlisted tools never appear in captures — mine `ProxyExchanges` for those.
- `Trace` is bimodal: rows before 2026-07-28 carry real stage traces; every newer row stores
  `[]`. Don't build anything on Trace.

`POST /api/v1/compression/preview` takes either `{captureId, enabledIds?}` — replaying the
real pipeline over a *stored capture* — or, since 2026-08-16 (plan_1A A3),
`{text, toolName, provider, command?, enabledIds?}`: the same pipeline over caller-supplied
text (null enabledIds = catalog defaults, `[]` = everything off, non-allowlisted tool → raw
text + `scopeAllowed:false`; provider is the stored wire key, matched exactly). Exchange-mined
candidate strings can now be judged against the real pipeline in one request, no traffic
reproduction needed.

## 3. Handling rules (non-negotiable)

- **This data never leaves the machine.** An exchange carries the system prompt, every user
  message, and the full contents of every file the agent read — strictly more sensitive than
  SessionLogs. Never checked in, never off-box outside the encrypted debug bundle, and never
  pasted wholesale into an LLM conversation (which is also self-defeating for a token saver).
  Reports may quote excerpts **hard-capped at ~500 chars**, and report/aggregate files live in
  gitignored locations (`*.db` and friends are already ignored repo-wide).
- **Read-only, always**: `sqlite3.connect('file:<path>?mode=ro', uri=True)` plus
  `PRAGMA busy_timeout`. WAL means readers don't block the live writer, but state.db has a
  lock-contention history — never open either DB bare/read-write while VibeRails runs.
- **Content passes cost the full file.** `Id`→`StatusCode` live before the big TEXT columns and
  are cheap to scan; touching `RequestBefore`/`RequestAfter`/`ResponseBody` — or anything stored
  after them, including the `Chars*` counters — walks each row's overflow chain. Budget for one
  streaming pass over ~18 GB that persists its aggregates (§6), not many exploratory passes.

## 4. Where to dig — the standing questions

Ranked by expected leverage as of 2026-08-16; reorder as answers land. **Before re-mining
any of these, check [`plans/`](plans/) — several already have measured answers there**
(first sweep: [`plans/plan_1A.md`](plans/plan_1A.md), incl. closed negative results —
don't re-litigate those without new data).

1. **Passthroughs.** Which (provider, tool, command-shape) buckets carry the most declined
   chars? Start from `RequestBefore = RequestAfter` rows, then raw==after tool outputs inside
   partially-rewritten requests. openai is 62% of traffic — Codex first.
2. **The metacharacter decline.** Every piped/chained command is `CommandShape.None` by rule.
   Measure how many chars ride on them; a safe sub-recognizer (e.g. last-segment-of-pipe) is
   only worth designing if the number is large.
3. **Non-allowlisted tools.** Codex diagnostic captures already list theirs (`exec` and
   `wait` graduated to the allowlist 2026-08-16 — plans/plan_1A.md; apply_patch et al
   remain); for Anthropic, mine exchanges for tool_use names outside
   Bash/PowerShell/BashOutput. (`Read`/`Grep` are scoped OFF by owner decision — see §5 —
   but Glob, WebFetch, MCP tool outputs etc. have never been measured.)
4. **The v1 no-op shapes.** `GitLog`/`GitDiff`/`DirectoryListing` classify and do nothing.
   Measure their volume before designing filters.
5. **Wire-guard rejections.** `RewriteAccepted=0 ∧ Changed=1` captures: how many chars of
   candidate savings die at re-serialization, and would a smarter emitter recover them?
6. **ESC/BEL/CR fail-opens.** How many large outputs abort whole-string? Quantify what
   `cr-collapse`/`ansi-strip` would recover — as evidence for Rob, **not** as a license to flip
   owner-preference defaults.
7. **Threshold tuning.** Distribution of output sizes vs. `truncate-long` (150/50, ≥10 lines,
   ≥4 KiB) and `dedupe-lines` (≥3) knobs. Cheap wins may hide just under thresholds.
8. **Response-side framing.** We never rewrite responses (and must not — model replies are
   untouchable); `ResponseChars` still frames what a request bought.
9. **Cross-request redundancy.** The same giant output resent every turn is the single largest
   byte pool, but "fixing" it is architecture, not a stage — and provider prompt caching
   already discounts resends. Measure honestly before dreaming.

## 5. Proposing a change — the bar it must clear

- **Evidence first**: chars at stake in both views (unique AND resend-weighted), sample
  capture GUIDs / exchange ids, failure modes you considered. The mining report *is* the
  proposal's exhibit A.
- **Invariants**: deterministic, idempotent, never-grows at the wire level, fail-open on
  anything surprising. Lossless = strict subsequence; lossy = explicit marker left behind.
  Idempotency is proven *cross-transform* — OutputMinifier's whole-string abort rules close
  those holes; do not "improve" them to copy-and-continue.
- **Shape discipline**: `CommandShape` extensions keep the first-token rule and allowlisted
  flags; unknown flag ⇒ decline. When in doubt, decline — a missed saving is free, a wrong
  rewrite is not.
- **Prompt-cache economics**: any change to what an enabled saver emits busts the CLI's
  provider prompt cache once per live session. Owner ruling (2026-08-16): this is a
  **non-concern for shipping decisions** — single-user app, changes land after a deploy, so
  every session hitting new saver behavior is fresh anyway. Don't batch or delay changes for
  cache reasons. (The determinism/idempotency invariants are a different matter: they protect
  the cache *within* a live session, every turn, and stand untouched.)
- **Owner decisions stand**: `cr-collapse`/`ansi-strip` OFF (terminal-shaped output),
  `scope-read`/`scope-grep` OFF (failed-Edit / line-number risk), one on/off switch per LLM as
  the only user surface. Bring Rob evidence; do not flip defaults, add settings, or rename ids
  on your own. Propose, don't presume.
- **Mechanics of a new stage**: catalog entry first (`CompressionCatalog.cs`), pipeline wiring,
  tests (see `Tests/TokenSaver/` + the `CuratedDefaults_*` pins), README stage-table update,
  History line here.
- **Graduation**: proposals that clear this bar get written up as an enhancement set in
  [`plans/`](plans/) (`plan_1A.md`, `plan_1B.md`, …) — evidence, exact change, risks,
  validation steps, and per-item Status lines that get updated as Rob decides. Parked ideas
  and closed negative results live there too, so they aren't re-mined from scratch.
- Savings math for humans: the UI's "tokens" = bytes/4 at display time only. Report chars/bytes
  and let the UI do its rounding.

## 6. The scripts

Home: `python-scripts/token_saver/` (plain `python x.py`, stdlib-only, never shipped, never
imported by the product). The corpus lives at `~/.vibe_rails/mining_corpus.db`.

| script | one-liner |
|--------|-----------|
| `build_corpus.py --name <you>` | One streaming pass over `ProxyExchanges` → deduped corpus (`Outputs` with raw + paired after-text + resend counts + shape/allowlist/ctl tags, `Exchanges` stats). Incremental via `Meta.last_src_rowid` — re-run anytime to ingest new traffic. Skips `ResponseBody` entirely (never SELECTed). |
| `corpus_stats.py --name <you>` | Descriptive report answering §4: passthroughs, tool ranking, decline reasons, shapes, ctl impact, size distribution → `results/<you>/corpus_stats_*.md`. |
| `experiment.py candidates/<x>.py --name <you>` | **The harness.** Runs a candidate transform over the corpus; reports savings (unique + resend-weighted, wire-guard-mirrored), loss accounting (content vs whitespace, Lossless/Reshaping/Lossy per output), invariant violations (idempotence, never-grows, determinism, exceptions) with samples, and top-wins/worst-losses diffs → `results/<you>/<x>_*.md` + `.json`. `--on after` (default) = incremental over the shipped pipeline; `--on raw` = against raw output. Filters: `--provider --tool --allowlisted-only --min-chars --limit`. |

A **candidate** is one Python file in `candidates/`: `transform(text, tool, command, provider)
-> str` (return input unchanged = decline; see `candidates/template.py` — copy it, don't edit
it). Python verdicts are prototypes; survivors get ported to C# and gated per §7.

**Multi-agent safety (yes, it's safe to run several agents at once).** `experiment.py` and
`corpus_stats.py` open the corpus **read-only** and write only under `results/<name>/`;
`--name` is **required** on every script precisely so concurrent runs never collide — use your
agent/run name. `build_corpus.py` is the only corpus writer and serializes itself via
`~/.vibe_rails/mining_corpus.build.lock` (held with an operating-system process lock; a second
builder is told who holds the lock and exits; a dead holder is released automatically). Readers are
unaffected mid-build (WAL snapshot).

**Change protocol (Rob's rule).** The core scripts (`mininglib.py`, `build_corpus.py`,
`experiment.py`, `corpus_stats.py`) are shared infrastructure: put changes in a **new file**
(e.g. `experiment_v2.py`), or **ask Rob whether other agents are running** before editing one
in place. New candidate files are always safe — one file per agent/idea, and never edit another
agent's candidate.

Other constraints (from §3): read-only URI opens, excerpt caps in reports, `results/` is
gitignored (reports quote real tool output — never commit them).

## 7. Validating a candidate end-to-end

1. Hypothesis + numbers from the mined report (§4 question, §5 evidence bar).
2. Turn on `TokenSaverCaptureEnabled`, drive real traffic that exhibits the pattern (or find
   existing capture rows) → capture GUIDs.
3. `POST /api/v1/compression/preview` with `enabledIds` variants against those GUIDs — or
   the `{text, toolName, provider}` form (plan_1A A3) straight from mined strings — the real
   pipeline, no simulation.
4. Implement behind a catalog id; `TokenSaverStageOverride` in settings.json is the hand-edit
   bisect hatch for live A/B (null = curated; a list replaces the curated set wholesale).
5. Unit tests + fixture; run the TokenSaver test suite.
6. After shipping: re-measure on fresh exchanges — the savings tally proves *that* bytes came
   off; only new exchange data proves your change did it.

## 8. History

- **2026-08-16** — Created (Claude session, with Rob). Context: the capture side finished
  landing 2026-07-28 (`ProxyExchanges`, always-on) and 2026-08-13 (Codex diagnostic-only
  captures); this file gives the mining half a durable home. `ProxyExchanges` at 20,750 rows /
  18.4 GB. Same day: §6 scripts built (`build_corpus.py` / `corpus_stats.py` /
  `experiment.py` + `candidates/`), multi-agent safe (read-only corpus, per-`--name` outputs,
  build lockfile), first full corpus built, seed candidates run.
- **2026-08-16 (later)** — First mining sweep done; **[`plans/plan_1A.md`](plans/plan_1A.md)**
  proposed: A1 allowlist Codex code-mode `exec` (~647 MB wire measured via curated-stage
  mirror — 61% of all tool-output wire chars ride on `exec`, hence Codex's 94% passthrough
  rate), A2 `wait` ride-along, A3 text-accepting preview endpoint. Parked with numbers:
  Read-family scopes (owner decision), metachar sub-recognizer (trailing-`2>&1` slice
  measured worthless), git-diff filter. Closed negatives: dedupe threshold 2, progress-spam
  stripper, Grep minify.
- **2026-08-16 (evening)** — plan_1A implemented in the working tree (Claude session, with
  Rob): A1 `exec` → scope-shell CodexTools, A2 `wait` → scope-shell-background CodexTools
  (`CompressionCatalog.cs` is the whole product change), A3 text form on the preview endpoint
  (handler extracted as `CompressionCaptureRoutes.PreviewAsync`); rewriter/route/settings
  suites 549/549 green. §5's cache-bust bullet rewritten per owner ruling (ship-time busts
  don't matter: single user, deploys recycle sessions). Not yet committed; §7 capture-audited
  soak + passthrough re-measure owed after deploy.
- **2026-08-16 (session end)** — Hand-off state written into
  [`plans/plan_1A.md`](plans/plan_1A.md) ("Where we left off"): the exact 7-file footprint,
  test commands (549/549 targeted filter; `-p:OutputPath` isolated-build dodge for the
  locked dev-instance bin), the 2 unrelated full-suite failures (Rob's concurrent edits, not
  plan_1A's), and the ordered soak steps. Measurement note for future estimates: the real
  minifier compresses ANSI-bearing exec output the Python mirror declines (confirmed via the
  fixture test), so `curated_stages_mirror.py` numbers are **floors** on ANSI-heavy tools.
