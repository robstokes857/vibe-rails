# Token Saving Plan — VibeRails LLM Proxy

**Purpose:** shrink the tokens a vendored CLI's model ingests by wiretapping the
one wire that actually carries them — the HTTPS call from the CLI to the model —
and stripping only *provably meaningless* bytes from tool output.

**Status:** v1 SHIPPED on branch `proxy_v1` (2026-07-12) — proxy plumbing, the four
allowlisted transforms, per-day savings tally in state.db, and the UI counter.
**Scope of v1:** Claude Code only. A handful of dead-safe transforms. Nothing clever.

Requirements that shaped the implementation: **span-based** string work (this is
C#/.NET 10 — it must be fast), **AOT-safe** (the app publishes NativeAOT; no
reflection JSON anywhere), and all proxy/token-saving business logic isolated in
its **own project** consumed through interfaces.

---

## 0. Prime Directive

> **Every transform must be invisible to the model.** If we have to think hard
> about whether it could change an answer, it does not ship.

Two rules that flow from that:

1. **Correctness ≫ savings.** A transform that saves 300 tokens but occasionally
   drops the one line the model needed is *negative* value — the model re-runs the
   command, gets confused, or produces a worse answer. That costs far more than 300
   tokens. We optimize for *never degrading the model*, and take the token savings
   as a side effect.
2. **Cheap-to-be-correct only.** If making a transform safe requires a bespoke
   per-command parser we have to babysit, it is disqualified from v1. Saving a few
   hundred tokens is not worth 20k tokens of us getting a parser right (and then
   re-getting it right every time the tool's output format drifts). **Whitelist,
   don't summarize.**

We are **not** building a summarizer, a truncator, or a "smart" output compressor.
We are building a lint pass that removes bytes a terminal itself would discard.

A third rule earned its place during implementation:

3. **Savings telemetry must never slow or risk the relay.** The tally write is
   fire-and-forget with failures swallowed (in exactly one place,
   `TokenSavingsStore`); a lost increment is acceptable, a stalled SSE turn is not.

---

## 1. The Litmus Test (a transform ships only if ALL are true)

- [x] **Lossless-for-the-model** — removes only bytes with no semantic content
      (trailing whitespace, color codes, overwritten progress frames). No words,
      numbers, paths, or structure ever disappear.
- [x] **Deterministic** — same input → same output, byte-for-byte, every time.
      (Non-determinism would thrash Anthropic prompt caching and *raise* cost.)
- [x] **Idempotent** — running it twice equals running it once. This is *provable*,
      not hoped-for: see the two abort rules in §3.
- [x] **Format-agnostic-safe** — never corrupts machine-readable output
      (`--porcelain`, `--json`, `-z`, diffs/patches, file contents).
- [x] **Pass-through by default** — anything we don't explicitly recognize as safe
      to touch is forwarded unchanged.
- [x] **Measurable** — every measured request reports `{bytes_before, bytes_after}`
      plus per-transform char counters; the seen-vs-rewritten gap is the canary for
      an upstream tool rename silently zeroing the feature.

---

## 2. Architecture (as built)

### The wire that matters
When Claude runs `git status`, the output does **not** go through our PTY. The
`claude` process reads it over a private pipe, packs it into the API request as a
`tool_result` block, and POSTs it to `api.anthropic.com`. That request body is
**structured JSON** — the tool output is sitting right there as a string.

So we intercept at the **network boundary**, not the PTY and not the exec layer:

```
claude CLI ──HTTPS POST /v1/messages──> [ VibeRails local proxy ] ──> api.anthropic.com
                                              │
                                    rewrite Bash tool_result strings
                                    (SSE response streamed back untouched)
```

This seat sees the exact bytes the model sees, is structured (JSON, not stdout
scraping), and **compounds**: tool output is re-sent in history every turn, so
shrinking it once saves on every subsequent turn and keeps prompt-cache prefixes
smaller.

### The `TokenSaver` project
All proxy + token-saving logic lives in its own AOT-clean class library,
`TokenSaver\` (net10.0, `IsAotCompatible`, AOT/trim analyzers on, **zero**
PackageReferences — ASP.NET types come from the `Microsoft.AspNetCore.App`
framework reference). The main app consumes it through three narrow seams, each
implemented by an adapter in `VibeRails\Services\LlmProxy\`:

| Seam (library) | Host adapter | Purpose |
|---|---|---|
| `ILlmProxyAuthGate` | `LlmProxyAuthGateAdapter(IAuthService)` | session/tab token validation |
| `ILlmProxyEventSink` | `LlmProxyEventSinkAdapter(IAppEventBus, ITokenSavingsStore)` | activity pings → UI, savings → state.db + one Serilog line, diagnostics → Serilog |
| `ILlmProxySettingsService` | `LlmProxySettingsService` (reads settings.json via `Config`) | one-snapshot settings reads |

Library contents: `LlmProxyRelay` (shared streaming relay), `LlmProxyRoutes` /
`LlmAnthropicProxyRoutes` (RequestDelegate-style endpoints — the minimal-API
Request Delegate Generator doesn't run in class libraries, so parameter-injected
lambdas there would silently fall back to reflection and break AOT),
`LlmProxyClaudeConfig` / `LlmProxyCodexConfig` (env/arg builders), and
`Minify\` — `OutputMinifier`, provider-specific `AnthropicMessagesRewriter` and
`CodexResponsesRewriter`, their body transforms, and pooled buffer plumbing.

### What we touch — and only this

Claude:

- Only `messages[].content[]` blocks of `type: "tool_result"` in a
  `POST …/v1/messages` JSON body ≤ 10 MB.
- Only results whose `tool_use_id` correlates to an earlier `tool_use` named
  **`Bash`** (exact match; the allowlist is a parameter — `BashOutput` is
  deliberately excluded from v1). Unknown tool / missing id → **pass through**.
- `content` as a string, or `{type:"text"}.text` entries inside a block array.
  Image blocks skipped.
- **Never** touched: file-read results (trailing whitespace can be significant —
  e.g. two trailing spaces = a Markdown `<br>`), `text` blocks, system prompt,
  `tool_use` inputs (whose nested JSON may contain look-alike `"type"`/`"content"`
  keys — the scanner skips them whole), the streamed model response, and every
  other byte of the request: the rewriter is a **raw-byte splice** (two-pass
  `Utf8JsonReader`, no DOM), so property order, client escaping, `cache_control`
  blobs, and unknown future fields are byte-identical by construction. Unchanged
  strings keep their original token bytes — the no-op path is byte-stable.

Codex:

- Only tool-output items inside a `POST …/responses` body's `input[]`: correlated
  `function_call_output` / `custom_tool_call_output` strings for allowlisted shell
  calls (`shell_command`, `exec_command`, `write_stdin`), plus provider-native
  `local_shell_call_output` / `shell_call_output` text fields.
- Correlation is exact on `call_id`; unknown calls and missing IDs pass through.
  Array-form output touches only `input_text.text`, while images/files and all
  non-output items remain byte-identical.
- The same raw-byte splice, fail-open, never-grow, deterministic, and idempotent
  guarantees apply. Prompts, messages, tool arguments, patch output, web results,
  and model responses are never candidates.

---

## 3. v1 Allowlist — what shipped

Each transform is an independent settings.json flag with per-transform removal
counters. All are **deletion-only** (output never grows → one pooled buffer of
`input.Length`), fused into a single line-oriented span scan in `OutputMinifier`.

| # | Transform | Why it's safe (zero semantic loss) | Flag (settings.json) |
|---|-----------|-----------------------------------|----------------------|
| 1 | **Carriage-return redraw collapse** — normalize `\r\n`→`\n`; per line keep only the final frame (last non-empty when the cursor parks at column 0, e.g. `"foo\r"`), and ONLY when that frame **provably covers** every earlier frame — visible-width comparison with SGR/OSC/BEL as zero width; tabs, cursor-move CSI, or unknown escapes make width unprovable. Unproven lines pass through **fully verbatim** (`\r` returns the cursor, it does NOT erase — `progress 99%\rdone` really shows `doneress 99%` on a terminal, so collapsing to `done` would delete visible text). | Collapse only happens when the overwrite demonstrably left nothing of the earlier frames — exactly what a human saw. Real progress bars (constant-width bars, growing counters, spinner→done) satisfy the proof; pathological partial overwrites don't and are kept whole. | `TokenSaverCollapseCrRedraws` (on) |
| 2 | **ANSI styling strip** — drop SGR (`ESC[…m`), OSC (both BEL and `ESC\` terminators), and bare BEL. Cursor-move/erase CSI and unrecognized ESC forms are **copied byte-verbatim** (stripping those safely needs full emulation → future). | Color, titles, and bells carry no meaning for the model; removing them never changes text or layout. | `TokenSaverStripAnsi` (on) |
| 3 | **Trailing whitespace strip** — spaces/tabs at end of each line. | Never load-bearing in command output. | `TokenSaverStripTrailingWhitespace` (on) |
| 4 | **Blank-line trim** — leading/trailing blank lines of the whole payload; sub-flag collapses runs of ≥3 interior blanks → 2. | Edge trim is fully safe; run-collapse is the only opinionated bit, hence its own flag, default off. | `TokenSaverTrimBlankLines` (on) / `TokenSaverCollapseBlankRuns` (**off**) |

The legacy Claude-specific kill switch is `ClaudeTokenSaverEnabled` (on; only active when
`ClaudeLlmProxyEnabled` is also on). Codex saving is active whenever its proxy is enabled and the
selected level is not `off`. Since 2026-07-13 the user-facing knob is the
**`TokenSaverLevel`** dropdown (§3.5); the per-transform flags above are honored
only at `TokenSaverLevel: "custom"` — they remain a bisection tool, not a user
decision.

**The three idempotency abort rules** (load-bearing — do not "improve" them):
1. A malformed/truncated escape sequence aborts the WHOLE string (returned
   untouched). Splicing around a partial sequence can fabricate a brand-new
   well-formed sequence a second pass would strip (`"\x1b[" + "\x1b[0m" + "m"` →
   `"\x1b[m"`). This includes an OSC containing an embedded ESC that isn't part
   of the ST terminator: a real terminal *exits* the OSC state there and renders
   what follows, so scanning on to a later BEL would delete visible text
   (adversarial-review finding, fixed 2026-07-12).
2. A deletion immediately after a kept bare ESC also aborts (`ESC + BEL + "[0m"`
   → `ESC[0m`).
3. With CR-collapse OFF, any string containing a bare CR (a `\r` not immediately
   followed by `\n`) aborts. T2/T3 deletions after a mid-line `\r` can make it
   line-final, and a second pass would reclassify it as CRLF framing or edge-trim
   it (`" \r"` → `" "` → `""`). This also makes the T1 kill switch honest: with
   it off, NO CR is ever removed, so flag bisection truly isolates T1
   (adversarial-review finding, fixed 2026-07-12).

All only fire on pathological input or bisection flag states; aborting costs
savings, never correctness. `Minify∘Minify == Minify` is enforced by tests for
all 32 flag combinations (corpus includes the review's counterexample shapes).

Note for archaeology: the original plan said "reuse `TerminalTextSanitizer`
(tested)". It turned out to have **no tests**, an allocation-heavy part model, and
the wrong keep-set semantics — so the library ships its own ~250-line span scanner
instead, pinned by a golden corpus with real ESC/CR bytes
(`Tests\TokenSaver\Fixtures\`). Those fixtures are `-text` in `.gitattributes`
(added 2026-07-13): `text=auto` was CRLF-mangling them at commit/checkout, which
silently broke every golden test on a fresh Windows clone.

---

## 3.5 Aggressiveness tiers & the lossy second stage (2026-07-13)

v1's transforms are display-invariant by design, which caps savings in the tens of
bytes per request. RTK-scale wins need transforms that are **lossy on formatting
or content** — so those ship in a separate stage, gated behind an explicit
user-chosen level. `TokenSaverLevel` (settings.json + the Settings-UI Tom Select
dropdown, `off/safest/safe/medium/high`) maps to a preset in
`TokenSaver\Minify\TokenSaverLevel.cs`:

| Level | MinifyFlags | Condense (lossy stage) | tool allowlist |
|---|---|---|---|
| off | — (no transform built) | — | — |
| custom | the 6 legacy flags | — | Bash, PowerShell |
| safest (default) | `Default` | — | Bash, PowerShell |
| safe | + blank-run collapse | — | + BashOutput |
| medium | same | dedup | same |
| high | same | dedup + truncation | same |

The lossy stage is `OutputCondenser`, run per string AFTER `OutputMinifier`
(`condense(minify(text))` inside the rewriter's splice loop):

- **D — consecutive-duplicate collapse** (medium+): a run of ≥3 identical lines →
  one instance + `" [xN]"` suffix. Idempotency keystone: marker-shaped lines are
  ineligible to start/join runs, so collapse output is a fixed point. Collapse
  only when strictly profitable (never grows).
- **T — head/tail truncation** (high): keep first 150 + last 50 lines, elide the
  middle as one `[... N lines elided ...]` line — only when the middle is ≥10
  lines AND ≥4096 chars (joint condition: a huge single-line JSON blob is never
  cut). Runs after D so lossless collapse gets first shot. Idempotent by
  arithmetic: output has exactly 201 lines → a re-pass sees a 1-line middle.
- **Whole-string abort**: any ESC/BEL/CR in the input → untouched
  (`AbortedControlChars`). Composes with the minifier's abort rules and keeps
  line edits away from anything the minifier kept verbatim.
- **NOT MinifyFlags members**: markers mean condenser output is not a subsequence
  of its input, which would void the minifier's 32-combo deletion-only proof. The
  condenser has its own options (`CondenseOptions`), its own stats
  (`CondenseStats`, net-chars conservation law), its own 4-combo property tests,
  and a cross-stage `P(P(x)) == P(x)` property + per-tier golden fixtures
  (`Tests\TokenSaver\Fixtures\Pipeline\`).

Litmus-test honesty: medium/high deliberately relax §1's
"lossless-for-the-model" plank — repetition counts and elided middles ARE
information loss. That is the entire reason they are opt-in tiers and not
defaults; every other plank (deterministic, idempotent, fail-open, never grows,
measurable) still holds and is property-tested. Switching level can bust a
provider prompt cache once, same as flipping a flag.

Allowlist notes: `PowerShell` is Claude Code's shell tool name on Windows
sessions — same content class as `Bash`, covered at every level. `Read`/`Grep`
stay excluded at every level: the model builds `Edit.old_string` from their
output, so touching them risks failed edits, not just lost savings.
Codex uses its own shell allowlist (`shell_command`, plus the earlier
`exec_command`/`write_stdin` pair); non-shell function/custom-tool outputs stay
excluded even though they use the same Responses API item envelope.

---

## 4. Explicit Non-Goals (name them so we don't backslide)

Still absolute at every level:

- ❌ **Stripping git hint lines** (`(use "git add <file>…")`) — the model sometimes
  acts on them; it's semantic.
- ❌ **Touching diffs / patches** — the model applies these; a byte off = broken edit.
  (Known accepted edge: a diff *displayed through Bash* still gets §3#3 trailing-space
  stripping — documented in the `unified_diff` golden fixture; transform 3's flag is
  the kill switch if dogfooding ever shows it matters.)
- ❌ **Touching file-read contents** — see the Markdown `<br>` footgun above.
- ❌ **Reformatting / re-indenting JSON or tables** — changes structure the model parses.
- ❌ **Per-command bespoke parsers** — the 20k-to-get-right trap. If a win needs one,
  it waits for an eval harness that proves it's neutral (see §7 Phase 2).
- ❌ **LLM-based summarization** of output — non-deterministic, thrashes the cache.

Graduated from this list into opt-in tiers (§3.5, 2026-07-13): deduping repeated
lines (medium) and truncating long output (high) — they still fail the *default*
bar on purpose and live behind the level dropdown.

---

## 5. Metrics & persistence (as built)

- **Per request** (only 2xx responses count — a rejected request never reached the
  model): the relay reports `{provider, bytesBefore, bytesAfter, toolResultsMinified,
  toolResultsSeen, per-transform char counters}` through `ILlmProxyEventSink`.
- **state.db**: `TokenSavings` table, one row per UTC day per provider, atomic
  upsert-increment (`Requests`, `RewrittenRequests`, `BytesBefore`, `BytesAfter`).
  Writer sets `PRAGMA busy_timeout = 5000` (the main `Repository` doesn't; this
  writer races background embed jobs). Write is **fire-and-forget, failures
  swallowed** (Prime Directive rule 3), and it yields off the caller's thread
  before touching SQLite (`await Task.Yield()` — without it, a synchronously
  completing semaphore made the whole DB round-trip run inline on the relay hot
  path, up to a 5 s busy-wait; adversarial-review finding, fixed 2026-07-12).
  The in-memory running total updates synchronously so the UI number never waits
  on SQLite.
- **Settings saves can't clobber the kill switch**: the settings POST handlers
  start from `Config.LoadFresh()` (not the process cache) before writing the
  whole Settings object back, so a hand-edited `ClaudeTokenSaverEnabled=false`
  survives unrelated UI saves (adversarial-review finding, fixed 2026-07-12).
  Known residual: an OLD build's save still drops flag keys it doesn't know.
- **UI**: the activity blinker's tally (`setTokensSaved`) seeds from
  `GET /api/v1/token-savings` at startup and updates live from `proxy_activity`
  pings (`bytesSaved` + `tokensSavedTotal`).
- **Tokens vs bytes**: exact bytes are stored; "tokens saved" = `bytes / 4`,
  derived server-side at display time only, so history reprices for free if a real
  tokenizer ever lands.
- **One log line per measured request** (counts only, never content):
  `Token saver: anthropic minified 2/3 tool results, 48231→31877 bytes (…
  dedup=N elide=N)` — the last two are the condenser's run/truncation counts.

## 6. Safety-critical proxy details (unchanged, all implemented)

- **Only the request is rewritten. The model's response is relayed verbatim** (SSE
  passthrough with response buffering disabled).
- **Determinism guards the cache.** The CLI re-serializes its own history every
  turn (it never sees our rewrite), and the rewrite is a pure function — so
  upstream receives byte-identical prefixes every turn. Idempotency covers any
  path where a previously-minified string comes back.
- **Fail open, everywhere.** Wrong method/path/content-type → not buffered.
  Declared body over 10 MB → streamed untouched. Chunked body outgrowing the cap
  mid-read → buffered prefix + live remainder stitched back together
  (`PrefixedBodyStream`). Malformed JSON → original bytes. Malformed escape inside
  one string → that string untouched (others still minify). Untranscodable string
  (lone-surrogate escape — which well-formed `JSON.stringify` emits when output
  truncation splits a surrogate pair — or invalid UTF-8) → that string spliced
  verbatim, no exception. Any unexpected exception → catch-all, original bytes,
  one Debug diagnostic.
- **Never grows, at the wire level.** Changed strings are re-emitted into a
  scratch buffer and size-checked first: the JSON encoder escapes non-BMP chars
  (a raw 4-byte emoji becomes a 12-byte `\uXXXX\uXXXX` pair), so a "minified"
  emoji-heavy string can come out larger — in that case the original token is
  spliced and no savings are claimed. `BytesSaved` can be zero, never negative.
- **Honest failure semantics on the relay.** Swallowed transport teardowns are
  classified: a genuine client disconnect ends quietly; an *upstream* failure
  before the response started returns a retryable **502** (never a fabricated
  empty 200); an upstream death mid-SSE aborts the client connection so the CLI
  sees a transport error, not a cleanly-terminated response missing its final
  events (adversarial-review finding, fixed 2026-07-12).
- **Never log secrets** (auth headers, request bodies). The activity ping carries
  method + query-free path + status + byte counts only.

---

## 7. Rollout

**Phase 0 — Plumbing, zero transforms. ✅ DONE.** Proxy passthrough shipped for
Claude (`ANTHROPIC_BASE_URL` + `ANTHROPIC_CUSTOM_HEADERS`) and Codex
(`model_providers` args); auth via session/tab headers; SSE streaming verified.

**Phase 1 — The allowlist, behind flags. ✅ CODE COMPLETE (2026-07-12).** All four
transforms on by default (run-collapse off). Golden corpus (8 realistic fixtures,
real ESC/CR bytes, hand-derived expected outputs), idempotency/determinism
property tests across all 32 flag combos, rewriter byte-splice tests, Kestrel
integration tests, tally tests. **Next: dogfood on this repo for a week; any weird
agent behavior → bisect by flag.** Watch the `Requests` vs `RewrittenRequests` gap
and the bytes-saved tally.

**Phase 1.5 — Aggressiveness tiers. ✅ CODE COMPLETE (2026-07-13).** §3.5: the
`TokenSaverLevel` dropdown, `OutputCondenser` (dedup medium+, truncation high),
PowerShell/BashOutput allowlist coverage. Default stays `safest` (= Phase 1
behavior); lossy transforms are user-opted. **Next: dogfood at high on this
repo** — watch for the model re-running commands because an elided middle
mattered, and for dedup markers confusing it.

**Phase 2 — Only if data justifies it.** Anything beyond §3/§3.5 requires an
**eval gate**: run a fixed task set with the transform on vs off and show
*identical* task outcomes before it's allowed near a user. No eval, no expansion.
Obvious first candidates once earned: Codex/Antigravity envelopes (same minifier
core, per-vendor "where's the tool output" adapters); char-level truncation of
huge single-line outputs; per-line (rather than whole-string) control-char
exclusion in the condenser.

---

## 8. Open Questions

- Does Pro/Max **OAuth** flow through a custom `ANTHROPIC_BASE_URL` cleanly?
  (Phase 0 dogfooding says yes for the setups tried; keep watching.)
- Does Claude Code ever send `/v1/messages` **without** `Content-Length`? The
  chunked-overflow path exists and is tested, but if it's dead code in practice we
  could simplify later.
- Real-world savings rate: the counters will tell us whether #1/#2 dominate as
  predicted and whether run-collapse (§3#4 sub-flag) is worth defaulting on.

---

### One-line summary
Sit on the CLI→model HTTPS call (now its own `TokenSaver` project), splice-rewrite
only shell `tool_result` strings. By default remove only bytes a terminal would
have thrown away anyway; at user-opted higher levels, deterministically collapse
repeated lines and elide the middles of huge outputs. Everything clever still
refused.
