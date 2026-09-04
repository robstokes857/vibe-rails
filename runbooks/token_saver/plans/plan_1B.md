# TokenSaver plan 1B — the Claude sweep (2026-09-03)

**Status: PROPOSED — nothing in the product changed. Rob decides.** The readable version with
the proofs spelled out and the suggestions ranked (config levers included) is
[`../findings_2026-09-03.md`](../findings_2026-09-03.md). Mining sweep run by a Claude
session (name `claude-mine-0903`) on the standing invocation in
[`../mining_runbook.md`](../mining_runbook.md), Claude-first, Codex second, per Rob's brief
("I am the only user, don't worry about legacy"). Living document: update the per-item Status
lines as things land or die, and add a line to `../mining_runbook.md` §8 when the state changes.

**Window:** 2026-07-29 → 2026-09-03 (37 days). `ProxyExchanges` 49,750 rows / 45 GB.
Corpus rebuilt incrementally (49,851 unique tool outputs, 188 MB unique / 7,548 MB wire). New
sequence dataset `~/.vibe_rails/mining_timeline.db` (33,068 requests, 1,792 conversations) built
by the new `scan_conversations.py` — see §8. Every number below is re-derivable from those two
files; nothing left the machine.

**Cost model used throughout.** "Wire chars" over-count (resends) and "unique chars" under-count
(cache writes cost 1.25×). Real usage came out of every response's `usage` block, so cost is
stated in **cost-equivalent tokens**: Claude `uncached×1 + cache-write×1.25 + cache-read×0.1 +
output×5`; Codex `(input−cached)×1 + cached×0.1 + output×8`. Chars→tokens at 3.6 chars/token
(calibrated on 222 full-tokenization turns; thinking blocks ARE billed on resend — including them
tightens the chars/token fit, CV 0.45 vs 0.52 without).

---

## 0. Headline

| | Claude | Codex |
|---|---|---|
| successful requests | 11,618 | 17,602 |
| cost-eq tokens, total / per active day | 328.7M / 9.67M | 323.0M / 8.73M |
| cache hit (reads ÷ all input) | 96% | 95.4% |
| cost-eq shares | cache reads 55%, cache writes 27%, output 17%, uncached 1% | cached 55%, uncached 26%, output 19% |
| request wire the saver removed | **0.65%** (≈40 MB of 6.9 GB) | 8.5% (905 MB of 10.7 GB) |
| ≈ cost-eq saved (gross) | ≈1.1M (0.35%) | ≈25M (7.8%) |
| `truncate-long` net of re-runs (§2) | **−3.9M** (it costs 3× what it saves) | +18.1M |

**For Claude the shell-output pipeline is at its ceiling, and its one big stage is net-negative.**
Allowlisted shell output is 9.3% of Claude's request wire; the saver removes 6.5% of *that*. The
other 90% of every Claude request is tool definitions (21%), Read output (10%), thinking (13%),
the model's own tool inputs (8%), and prose — none of which a tool-result rewriter may touch. The
three biggest Claude levers found are therefore **outside** TokenSaver (§1, §3), and the one
inside it is a *retirement*, not a new stage (§2, B1).

## 1. Where Claude's tokens actually go

Composition of the 9,352 main-conversation requests (the ones carrying tool definitions),
char-weighted, 6,276 MB total:

| slice | MB | share | touchable? |
|---|---|---|---|
| tool definitions (`tools[]`) | 1,329 | **21.2%** | no — but see B2 |
| tool results, all | 1,339 | 21.3% | |
| · of which allowlisted shell (Bash/PowerShell) | 584 | 9.3% | yes — the whole pipeline |
| · of which `Read` | ≈646 | ≈10% | owner-parked (scope-read OFF) |
| thinking blocks (billed, see cost model) | 840 | 13.4% | no (model replies) |
| tool_use inputs (Write content, Edit strings…) | 485 | 7.7% | no (model replies) |
| assistant text | 250 | 4.0% | no |
| user text (of which CLI `<system-reminder>`s 107 MB) | 180 | 2.9% | no |
| system prompt | 113 | 1.8% | no |

**Tool definitions: 142 KB per request on average (59 tools), 181 KB / 68 tools at the top.**
Itemized from one 2026-08-27 request: built-ins 34 tools / 146 KB (Artifact 27 KB, Workflow
21 KB, DesignSync 9 KB, PowerShell 8.6 KB, Monitor 7.5 KB, …), `claude-in-chrome` 22 tools /
27 KB, viberails-mcp 6 / 3.3 KB, the three Google connectors 6 / 4.5 KB. At 0.1 per cache read
that slice is ≈37M cost-eq over the window — **11% of Claude spend, ≈1M/day, roughly 30× what
TokenSaver saves on Claude.** The MCP part (≈19% of it, ≈7M) is the only part Rob controls.

**The auto-mode security classifier is a request class of its own:** 2,160 requests (18.6% of
all Claude requests) to `claude-sonnet-5` with a 125 KB system prompt ("You are a security
monitor for autonomous AI coding agents"), no tools, ~82k cache-read tokens each — **32.0M
cost-eq, 9.7% of Claude spend.** Invisible to TokenSaver (it is a system prompt), see B3.

## 2. Re-runs — Rob's README question, answered

> "Look for re-runs. When the LLM had to call again to get something we trunked."

Method: `scan_conversations.py` keeps, per conversation, the ordered tool timeline of the request
that carried the most tool calls (raw/after chars + which saver markers each result carried), and
`rerun_economics.py` classifies every tool call in the assistant turn *immediately after* an
elided result: exact re-run, narrowed re-run (same tool, shared path-like token, a re-fetch head
such as `sed`/`head`/`tail`/`git`/`grep`), `Read` of a file named in the elided command, or a
`pause_token_saver` call. The elision's saving is charged at 1.25 (write) + 0.1 per remaining
resend; a re-run is charged as one extra turn at that conversation's average turn cost plus the
re-fetched output.

| provider / family | elisions | re-runs | saved unique | saved wire | saved cost-eq | re-run cost-eq | **net** |
|---|---|---|---|---|---|---|---|
| Claude · file dump (cat/sed/for…) | 78 | 58 (74%) | 733 K | 25.8 MB | 951 K | 4,276 K | **−3,325 K** |
| Claude · git diff/show/log | 34 | 27 (79%) | 253 K | 9.0 MB | 331 K | 921 K | **−590 K** |
| **Claude · all** | **114** | **86 (75%)** | 998 K | 35.0 MB | 1,290 K | 5,222 K | **−3,932 K** |
| Codex · file dump | 288 | 95 (33%) | 4,485 K | 374 MB | 11,833 K | 3,567 K | +8,266 K |
| Codex · git diff/show/log | 92 | 28 (30%) | 1,730 K | 103 MB | 3,405 K | 1,128 K | +2,277 K |
| Codex · grep | 68 | 2 (3%) | 1,216 K | 88 MB | 2,840 K | 117 K | +2,723 K |
| **Codex · all** | **593** | **161 (27%)** | 10,450 K | 798 MB | 25,494 K | 7,389 K | **+18,105 K** |

The re-run classifier is a heuristic; a stricter first version (exact command or ≥2 shared
tokens) still found 46/114 (40%) Claude re-runs and a net of −3.3M. Break-even for Claude is a
re-run rate of ≈19% — the "noticed" cases alone (the model's own text says *"153 lines were
elided. Let me read the full …"*) are 11%. Representative Claude sequences (paths generalized):

- `git diff -- src/backend-manager.ts` → 153 lines elided → next turn `git diff -U2 -- … | sed -n '150,340p'` (7.2 KB fetched)
- `git diff -- wwwroot/style.css` → 140 lines elided → next turn writes the diff to a temp file and `Read`s it (the Read is not compressed, so the elision saved nothing and cost a turn)
- `cat TokenSaver/Shape/CommandShape.cs` → 233 lines elided → `Read` with offset 150 (this was the 2026-08-29 investigation biting itself)

**Why Claude and Codex differ.** (a) Claude Code caps every Bash result at 30,000 chars *before*
the proxy sees it (max observed 29,307; the CLI's own `… [N characters truncated] …` note
appears in 3 outputs), so `truncate-long`'s catastrophic-payload job is already done upstream
and it only ever trims 4–30 KB outputs the model explicitly asked for. Codex exec output is
uncapped (40 KB `Get-Content -Raw` dumps are routine). (b) A Claude turn is ≈5× dearer: long
Opus/Fable contexts (avg 132 messages on Opus 5) at 0.1 per cached token, output at 5×.
(c) Claude re-fetches what it lost (75–79%); Codex mostly moves on (27–33%) — which for a file
dump is its own correctness worry, and is why the 2026-08-29 widening is not proposed for
reversal here.

Since the 2026-08-29 file-read widening the remaining Claude trigger is `git diff`/`show`/`log`
(the known gap in `../truncation_file_reads.md` §6): 79% re-run rate, net −590 K on its own.

`dedupe-lines` (33 Claude markers) and `elide-passed-tests` produced no re-fetches attributable
to the marker (the "re-runs" after dedupe were the model editing and re-running its own script).

## 3. Cache misses — TokenSaver is exonerated, the real causes are named

50% of Claude's cache-write tokens (35.4M of 71.2M) come from 285 requests that re-wrote more
than 50k tokens at once. Because a non-deterministic rewrite would show up exactly there, every
big miss that followed another request of the same conversation within 5 minutes (64 cases) was
diffed byte-for-byte against its predecessor, original vs upstream body:

- **Zero** cases where the upstream (rewritten) prefix diverged at a tool_result the original
  did not diverge at. Every divergence was the CLI moving its `cache_control` breakpoint, a
  tools-list change, or a model change. The pipeline's determinism holds on live traffic.
- **Tools-list churn (the expensive one):** MCP servers connecting/disconnecting mid-session
  rewrite the *entire* conversation (tools precede messages in the prefix). Seen: viberails-mcp
  tools vanishing when the dev instance restarts (155k tokens), `claude-in-chrome` dropping and
  returning 32 s later (262k + 277k tokens — one blip, ≈675k cost-eq), the Google connectors'
  6 auth tools appearing late (3 × 65–96k), and the `Bash`/`WebFetch` tool *descriptions*
  changing mid-conversation (3 × ≈90k — dynamic descriptions).
- Model switches mid-conversation: 10 cases (`/model`, fast mode), ≈1M tokens. Rob's choice.
- 1-hour TTL expiries (`cache_control.ttl = "1h"` confirmed): 58 cases after 1 h+ idle,
  13.6M tokens ≈ 17M cost-eq (5% of Claude spend). Unavoidable short of keeping sessions warm.
- Remaining "prefix-stable" misses (≈17) are server-side or CLI-side (a run of Fable-5 turns on
  2026-08-21 re-wrote 74–129k tokens every turn while reading a constant 46.5k — the
  system+tools prefix only). Not ours; noted for Rob.

Health overall: on mature turns (≥10 messages) 92% of Opus-5 and 80% of Fable-5 turns are
<5% miss; the >50%-miss bucket is 133 turns / 20.5M tokens ≈ 7.8% of Claude spend.

## 4. The shell pipeline is at its ceiling (Claude)

Bash is 582 MB wire / 15.6 MB unique. By command head (leading `cd X &&` stripped): `sed` 26%,
`cat` 16%, `grep` 15%, `git diff` 14%, `echo` 6%, `for` 3%, `ls` 3%, `git show` 2%. So **41% of
Claude's shell output is file contents read through the shell** (the auto-mode harness tells the
agent to read with `sed -n`/`cat`), 15% is grep through pipes, 16% is diffs. The biggest size
bucket — 4–16 KB outputs of 50–200 lines, 178 MB wire, 31% of Bash — is untouched by every lossy
stage by design (under the 200-line floor) and is exactly what the model asked for.

Control characters are a non-issue on Claude: 1,089 of 1,149 ctl-bearing Bash outputs are
CRLF-only (handled by `crlf-normalize`); ESC appears in 35 (3.8 MB wire). Shape stages are
irrelevant on Claude: 84 recognized grep outputs / 4.6 MB wire all-time; the `cd DIR && cmd`
carve-out would rescue **3** grep outputs; piped/`\|`-alternation greps carry the rest.

## 5. Proposals

### B1 — Retire `truncate-long` for the Anthropic provider  ← the one TokenSaver change

**Evidence:** §2. Net −3.9M cost-eq over 37 days (≈ −105 K/day) and 35 MB of the 41 MB wire the
saver "saved" on Claude was this stage — the Claude savings meter has been counting a loss.
Claude Code's 30k cap already bounds payloads; post-2026-08-29 the stage only fires on
`git diff`/`show`/`log` on Claude, at a 79% re-fetch rate.

**Change (sketch, not implemented):** stages get a per-provider default. Today
`LlmProxySettings.TokenSaverPlan` is one `CompressionPlan` for all providers, resolved by
`CompressionCatalog.Resolve(TokenSaverStageOverride)`. Add `OffByDefaultFor: ["anthropic"]` to
`truncate-long`'s `CompressionStageInfo`, make `Resolve(selection, provider)` honour it (an
explicit `TokenSaverStageOverride` list still replaces the curated set wholesale, all
providers), and have the settings service hand each rewriter its provider's plan (four plans, or
`PlanFor(provider)`). Captures then record the honest per-provider `EnabledIds`. Wire-format ids
untouched; README stage table + `CuratedDefaults_*` pins get a per-provider variant.

**Narrower alternative:** treat `git diff`/`show`/`log` as file reads in
`CommandShapes.ReadsFileContents` (all providers). Fixes 34 of Claude's 114 elisions and would
cost Codex ≈2.3M net (its diff elisions are net-positive at a 30% re-run rate). The provider-
specific form is the one the data supports.

**Validation:** after deploy, `rerun_economics.py anthropic` on fresh conversations should show
0 elisions; the Claude savings tally will drop ≈85% — that is the expected, correct outcome.

**Status: proposed 2026-09-03.**

### B2 — Tool-definition bloat and churn (Rob's Claude Code config, not TokenSaver)

≈37M cost-eq (11% of Claude spend) is tool definitions re-read from cache every turn; the MCP
portion (≈7M) is the controllable part, and MCP *churn* mid-session costs ≈250–540k tokens per
blip (§3). Options, none of them TokenSaver: connect `claude-in-chrome` (22 tools, 27 KB) only
in sessions that drive a browser; check whether the Google connectors (6 auth-only tools) need
to be attached to this project at all; keep the in-process viberails-mcp server alive across dev-
instance restarts *or* accept that every restart double-busts every open Claude session; verify
whether tool deferral (the `ToolSearch` mechanism the system prompt already mentions) keeps
deferred MCP tools out of `tools[]` — sessions with 30 tools / 85 KB exist in the data, so some
configuration already achieves it. **Status: finding for Rob.**

### B3 — The auto-mode classifier (Claude Code behaviour, not TokenSaver)

32M cost-eq (9.7%) across 2,160 classifier calls. The lever is fewer classified actions:
permission allow-rules for the routine read-only commands (`/fewer-permission-prompts` exists for
this) — if auto mode consults the classifier only for actions no rule covers, that is ≈15k
cost-eq per avoided call. Worth a week's A/B: count `claude-sonnet-5` no-tools requests per day
before and after. **Status: finding for Rob.**

### B4 — `ansi-strip` ON for Codex only (small, free)

The condenser fails open whole-string on any ESC, so 590 ESC-bearing exec outputs never see
dedupe/truncate. Candidate `candidates/ansi_strip_then_curated.py` (SGR/OSC/BEL only, declines on
cursor moves, then the curated mirror) on `--provider openai --tool exec --on raw`: fired 590,
**1.62 MB unique / 43.6 MB wire (≈1.2M cost-eq)**, invariants OK, 29 lossy (condense unlocked).
On Claude the same candidate fires 32 times for 0.5 MB wire — nothing, which is why this is
Codex-only and does not touch the owner's terminal-output preference for Claude. Needs the same
per-provider default mechanism as B1. **Status: proposed 2026-09-03.**

### B5 — Codex: the 2026-08-29 widening has a price; keep it anyway

Whole-request savings on Codex went from 15–28%/day (8/18–8/29) to 6–9%/day after file reads
were preserved; the "file dump" family had been net **+8.3M** cost-eq for Codex (33% re-run
rate). The other 67% of the time Codex proceeded without the middle of a file it had asked to see
— the correctness risk `truncation_file_reads.md` was written for. No reversal proposed;
re-measure with `rerun_economics.py openai` once a few weeks of post-fix Codex traffic exist.
**Status: note.**

## 6. Codex, briefly

10.7 GB of request wire: tool results 53% (code-mode `exec` 5,478 MB wire, 131 MB unique),
encrypted reasoning items 17%, developer/user text 11%, tool inputs 5%. Passthrough was 100%
from 8/1 to 8/16 (code-mode only, exec not yet allowlisted), 2–10% since plan_1A deployed
(8/17–18). Cache hit 95.4%. The saver removes 8.5% of wire; truncation is net-positive (§2).
Duplicate results within a conversation: `list_agents` 45× (7.8 MB wire) — trivial.

## 7. Closed — negative results on record (don't re-mine without new data)

| idea | measured | verdict |
|---|---|---|
| Read minify-only (crlf/trailing-ws/blank-edges/blank-runs, no condense) — the variant plan_1A parked without a number | 1,962 of 1,992 Read outputs fire: **30.8 K chars unique / 1.58 MB wire** (0.24% of Read wire) | worthless; scope-read stays off for savings reasons too |
| `cd DIR && <cmd>` shape carve-out | 3 grep outputs; direct greps 64 outputs / 2.8 MB wire, grep-group mirror saves 0 | worthless |
| grep-group on Claude Bash greps | 89 MB wire of grep, ~all piped or `\|`-alternated (metachar) | no sound recognizer; closed |
| duplicate tool results within a conversation (dedupe a repeat Read/Bash against the earlier identical result) | Read 1, Bash 8, Edit 6 all-time | Claude Code already suppresses re-reads ("file state is current"); closed |
| ctl-char fail-open on Claude | 1,089/1,149 CRLF-only; ESC 35 outputs | non-issue |
| ansi-strip / cr-collapse for Claude | 32 outputs, 0.5 MB wire | nothing to win; owner preference moot |
| non-allowlisted Claude tools other than Read/Grep, since 8/16 | ExitPlanMode 4.2 MB, TaskOutput 2.9, Agent 2.2, Glob 2.2, browser_batch 2.0, Write 1.9, Edit 1.5 MB wire | nothing worth a scope |
| Grep tool scope (owner-parked) | 9.0 MB wire since 8/16 | nothing worth arguing about |
| minified shell dumps breaking Edit `old_string` | Edit failure 1.3% overall; 1/28 after a saver-changed dump, 1/103 after an untouched one, 3/433 after `Read` | no signal (n small); watch, don't act |
| thinking blocks as a pool | 840 MB, billed | untouchable (model replies) |

## 8. How to re-run this

All read-only on the live DBs, stdlib only, `--name` required, results under
`python-scripts/token_saver/results/<name>/` (gitignored).

```
cd python-scripts/token_saver
python build_corpus.py --name <you>                 # incremental corpus (2 min for 28k rows)
python scan_conversations.py --name <you>           # incremental timeline DB (4 min for everything)
python corpus_stats.py --name <you>                 # §4-style tables
python timeline_stats.py --name <you> --provider anthropic     # §1, §3 health, reactions, elisions/day
python rerun_economics.py --name <you> --provider anthropic    # §2 table
python experiment.py candidates/read_minify_only.py --name <you> --provider anthropic --tool Read --on raw
python experiment.py candidates/ansi_strip_then_curated.py --name <you> --provider openai --tool exec --on raw
```

One-off scratch scripts (not kept; the shapes are cheap to re-derive from `Requests` +
`Conversations`): tool-definition itemization = `json.loads(RequestBefore)["tools"]` of one
request picked via `Requests.ToolsDefChars`; cache-miss forensics = for each request with
`CacheCreationTokens > 50000` and a same-`ConvKey` predecessor within 5 min, common-prefix
`RequestAfter` vs predecessor and `json`-compare `system`/`tools`/`model`/`messages[:-2]`;
Edit-failure attribution = walk each timeline keeping the last tool that showed each basename
(`Read` / shell dump with `aft != raw` / `Write`) and bucket `Edit` items with `err = 1`.

## Where we left off (2026-09-03, session end)

- Nothing committed. New files: `python-scripts/token_saver/scan_conversations.py`,
  `timeline_stats.py`, `rerun_economics.py`, `candidates/read_minify_only.py`,
  `candidates/ansi_strip_then_curated.py`, this plan; edits to `../mining_runbook.md`
  (§2c/§4/§6/§8) and a one-line pointer under Rob's note in `TokenSaver/README.md`.
- B1 and B4 want the same per-provider stage-default mechanism; B2/B3 are config findings.
- `~/.vibe_rails/mining_timeline.db` is a derived file; delete and rebuild at will.
