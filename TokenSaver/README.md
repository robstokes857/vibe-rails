# TokenSaver

TokenSaver is the library that sits between a coding CLI (Claude Code, Codex,
OpenCode) and its model provider, and makes the request smaller on the way up.

It is a **library**, not a service. It is compiled into `vb.exe` and its routes are
mapped into the same Kestrel host as the rest of VibeRails. There is no separate
process, no separate port, and no way to run it standalone.

- [The one-paragraph version](#the-one-paragraph-version)
- [Where the compression actually lives](#where-the-compression-actually-lives)
- [The stage catalog](#the-stage-catalog)
- [The invariants, and why they are not negotiable](#the-invariants-and-why-they-are-not-negotiable)
- [Following one request through the system](#following-one-request-through-the-system)
- [Debugging: how to watch a request compress](#debugging-how-to-watch-a-request-compress)
- [Captures](#captures)
- [Settings](#settings)
- [Project layout](#project-layout)
- [Tests](#tests)
- [Things that will bite you](#things-that-will-bite-you)

## The one-paragraph version

The CLI is pointed at a local proxy instead of `api.anthropic.com`. Every request
flows through `LlmProxyRelay` untouched — except POSTs to `/v1/messages`, which get
buffered, parsed as JSON, and have their **`tool_result` strings** rewritten in
place. Nothing else in the body is touched: not the system prompt, not the user's
messages, not the model's replies. Only the output of tools, and only tools on an
explicit allowlist. Then the body is re-serialized and forwarded. If literally
anything goes wrong at any point, the original bytes are forwarded instead.

That last sentence is the design. **The worst outcome of any bug in this library
should be a request that saves nothing.** Everything below exists to keep that true.

## Where the compression actually lives

This is the part worth knowing. Read these five files in this order and you have the
whole thing:

| # | File | What it is |
| - | ---- | ---------- |
| 1 | **`Pipeline/CompressionCatalog.cs`** | **The source of truth.** Every stage, its id, its default, its order. The settings UI, the what-if preview, and this README all render from here. Adding a stage starts here. |
| 2 | **`Pipeline/CompressionPipeline.cs`** | **The whole compression path for one string.** If you set one breakpoint, set it on `Run`. There is no other path. |
| 3 | **`Minify/OutputMinifier.cs`** | Stages 1–6, fused into one forward scan. The lossless pass. Its class comment is the real spec — read it before touching a byte. |
| 4 | **`Shape/ShapeFilters.cs`** | Stages 7–10. Rewrites the output of a *recognised command*: regroups `git status --short`/`rg -n`/`find`, elides passing tests from a recognised test runner. |
| 5 | **`Minify/OutputCondenser.cs`** | Stages 11–12, fused. The lossy pass — dedupe runs, elide long middles. |

And the three files that decide **which strings ever reach the pipeline**:

| File | What it is |
| ---- | ---------- |
| `Minify/AnthropicMessagesRewriter.cs` | Walks the Anthropic JSON body, finds `tool_result` blocks, matches each to the `tool_use` that produced it (for the tool name **and the command text**), and splices rewritten strings back in. |
| `Minify/CodexResponsesRewriter.cs` | Same job for Codex's `/responses` shape. |
| `Minify/ChatCompletionsRewriter.cs` | Same job for the OpenAI Chat Completions shape used by OpenCode's zai/Z.AI (GLM) and xai (Grok) providers. |

Everything else in this project is plumbing: relays, buffers, auth gates, settings
seams. If you are debugging *compression*, it is one of the eight files above.

### The mental model

```
                    the CLI                                    api.anthropic.com
                       |                                              ^
                       v                                              |
   LlmAnthropicProxyRoutes  ->  AnthropicBodyTransform  ->  LlmProxyRelay
                                        |                             ^
                          buffers body, |                             | rewritten
                          then hands to |                             | (or original) bytes
                                        v                             |
                          AnthropicMessagesRewriter --------------------
                                        |
                       for each allowlisted tool_result string:
                                        |
                                        v
                          +----------------------------+
                          |   CompressionPipeline.Run  |   <-- BREAKPOINT HERE
                          +----------------------------+
                                        |
             1-6  OutputMinifier (fused) |  lossless
             7-10 ShapeFilters           |  reshaping + test elision
             11-12 OutputCondenser (fused)| lossy
                                        |
                                        v
                          rewritten string + StageTrace[]
                                        |
                                        v
                          ICompressionCaptureSink  ->  state.db
```

## The stage catalog

Defined once in `Pipeline/CompressionCatalog.cs`. This table is generated from that
file by hand — if they disagree, **the code is right and this table is stale**.

Order is fixed and is not a user preference, because the stages don't commute:
cleanup must run before shape filters (so they don't have to parse ANSI), and dedupe
must run before truncation (so lossless collapse gets first shot at bringing the
payload under the truncation threshold).

| # | Id | Kind | Default | What it does |
| - | -- | ---- | ------- | ------------ |
| 1 | `cr-collapse` | Lossless | **off** | Keeps the final frame of a `\r`-redrawn line (progress bars, spinners), only when that frame provably covers every earlier one. Also normalizes CRLF→LF. |
| 2 | `crlf-normalize` | Lossless | on | Rewrites Windows `\r\n` line endings to `\n`. Every later stage fails open on a surviving `\r`, so without this the group, elide, dedupe and truncate stages all no-op on output from rg, PowerShell and dotnet. Split out of `cr-collapse` (2026-07-28) so CRLF normalization is on by default while `cr-collapse` stays off. |
| 3 | `ansi-strip` | Lossless | **off** | Drops SGR colour, OSC titles, BEL. Cursor moves/erases kept verbatim. |
| 4 | `trailing-whitespace` | Lossless | on | Spaces/tabs at end of line. |
| 5 | `blank-edges` | Lossless | on | Blank lines at the start/end of the payload. |
| 6 | `blank-runs` | Lossless | on | Runs of 3+ blank lines → 2. |
| 7 | `git-status-group` | Reshaping | on | Groups porcelain `XY path` lines under one header per status. |
| 8 | `grep-group` | Reshaping | on | Groups `path:line:content` under one header per file. |
| 9 | `find-group` | Reshaping | on | Groups a flat path list under one header per directory. |
| 10 | `elide-passed-tests` | Lossy | on | Runs of individual passing-test lines from a recognised test runner → one `[... N passed ...]` marker. Failures/errors/skips/logs/summaries kept verbatim, in place. |
| 11 | `dedupe-lines` | Lossy | on | 3+ identical lines → one, tagged `[xN]`. |
| 12 | `truncate-long` | Lossy | on | Keeps first 150 + last 50 lines, elides the middle. |

**Why are `cr-collapse` and `ansi-strip` off by default?** They are the two largest
lossless wins available, and they are off by deliberate product decision (2026-07-15,
reaffirmed 2026-07-18): they reshape terminal-looking output, and the owner wants to
opt into that consciously rather than inherit it. This is a preference, not a safety
finding. Do not "fix" it by flipping the defaults.

**Why are the three lossy stages on by default?** Curated-set decision (2026-07-18/19):
they are the largest wins on the payloads that actually burn tokens (log/build/test
spew), and each leaves an explicit marker (`[... N passed ...]`, `[xN]`,
`[... N lines elided ...]`) where content was removed, so the model always knows
something is missing and can re-run the command with a narrower filter. The default
set is pinned by `LlmProxySettingsServiceTests.CuratedDefaults_*`.

**`elide-passed-tests` earns its place twice.** A green test's only information is
that it passed, so per-test pass lines (pytest -v, jest/vitest `✓`, dotnet test,
go test -v, cargo test — commands recognised in `CommandShape.Classify`, wrappers
and pipes declined) compress to a counting marker at near-zero information cost.
And because it runs *before* `truncate-long`, it fixes that stage's worst case on
test output: without it, a big suite's failure section can land in the truncated
middle while 150 lines of passes survive at the head.

### Scopes — a separate axis

Stages say *how* we compress. Scopes say *what we are allowed to touch*. They are
separate because they fail in completely different ways, and conflating them makes
"why did my Edit break?" unanswerable.

| Id | Default | Tools |
| -- | ------- | ----- |
| `scope-shell` | on | `Bash`, `PowerShell` |
| `scope-shell-background` | on | `BashOutput` |
| `scope-read` | **off** | `Read` |
| `scope-grep` | **off** | `Grep` |

**`scope-read` is the dangerous one.** The model builds Edit `old_string` values out
of Read output. If we rewrite Read output, the model constructs an `old_string` that
does not exist in the file and **the edit fails**. That is a correctness bug, not a
lost saving. It ships off, and it should stay off until captures prove otherwise.
`scope-grep` is milder — the model navigates by those line numbers rather than
quoting them — but has not been proven either.

Unknown tool names always fail toward *no savings*: they are counted in the `seen`
counter and never rewritten. So if Anthropic renames `Bash` tomorrow, savings quietly
drop to zero and the seen-vs-minified gap is the canary. Nothing corrupts.

## The invariants, and why they are not negotiable

Every stage must be **deterministic**, **idempotent**, and **never-grows**. These
aren't taste; each one is load-bearing:

- **Deterministic** — a pure function of the string. Not of the time, not of the
  request count, not of what we saw last turn.
- **Idempotent** — `f(f(x)) == f(x)`.
- **Never grows** — if the output is longer, the original is used.

The reason is **prompt caching**. The CLI re-sends its entire conversation history on
every single turn. If a `tool_result` from turn 3 compresses to different bytes on
turn 9 than it did on turn 4, the provider's cache breaks at turn 3 and *everything
after it re-processes at full price*. A transform that drifts doesn't just lose
savings — it costs more than doing nothing at all. This is why the answer to "can we
compress older history more aggressively?" is **no**: age-based tiering is
non-deterministic by construction and would thrash the cache on every turn.

Both fused passes enforce these with **whole-string fail-open** rules that look
paranoid and are not:

- A malformed/truncated escape sequence aborts the **entire string**. Splicing around
  a partial sequence can *fabricate* a well-formed one that a second pass would then
  strip: `"\x1b[" + "\x1b[0m" + "m"` → `"\x1b[m"`. Not idempotent.
- A deletion immediately after a kept bare ESC aborts, for the same reason:
  `ESC + BEL + "[0m"` → `ESC[0m`.
- With `cr-collapse` off, any bare CR aborts. With T1 disabled, bare CRs are inert
  content — but a T2/T3 deletion can make one line-final, and the next pass would
  reclassify it as CRLF framing and strip more.

`OutputMinifier`'s class comment says *"Do not 'improve' any of these rules to
copy-and-continue."* It means it. Every one of those rules is closing a specific
idempotency hole, and each hole is a cache-thrash bug that would be nearly impossible
to diagnose from the symptom (a slow, expensive session).

## Following one request through the system

1. **`LlmAnthropicProxyRoutes.cs`** maps the proxy endpoint. Auth-gated
   (`ILlmProxyAuthGate`) — session **and** tab, not session alone.
2. **`AnthropicBodyTransform.TryTransformAsync`** decides whether this request is even
   a candidate: POST, path ends `/v1/messages`, JSON content type, body present.
   Anything else returns `null` → the relay streams it untouched.
3. It buffers the body into a pooled `BufferLease`, up to `MaxBufferedBodyBytes`
   (10 MB). A body that outgrows the cap mid-read is stitched back together with
   `PrefixedBodyStream` and forwarded unrewritten — the prefix is already off the
   wire, so it can't just be dropped.
4. **`AnthropicMessagesRewriter.Rewrite`** does the real work:
   - `LocateToolResults` walks the JSON once, collecting (a) a map of
     `tool_use_id → tool name`, (b) the command text for shell tools, and (c) the byte
     ranges of every `tool_result` string.
   - Entries whose tool isn't in the scope allowlist are skipped (counted, not touched).
   - For each surviving string: unescape → **`CompressionPipeline.Run`** → re-escape
     into a scratch buffer → **size check** → splice.
5. The size check is not redundant. The JSON encoder escapes non-BMP chars as
   surrogate pairs (a raw 4-byte emoji becomes 12 bytes), so a *shorter* string can
   re-serialize *larger*. When that happens the original token is spliced instead.
   The never-grows promise is enforced at the **wire**, not just in char-space.
6. The relay sends the rewritten bytes upstream and disposes the `BufferLease` — not
   before, because the forwarded `HttpContent` reads straight out of those pooled
   buffers.
7. `ILlmProxyEventSink.SavingsMeasured` reports byte counts (never content) for the
   tally. `ICompressionCaptureSink.Capture` reports the full before/after (see below).

## Debugging: how to watch a request compress

**The short version: breakpoint `CompressionPipeline.Run`. That is the entire
compression path for one string. There is no other.**

Because this is AOT there are no MVC controllers, no filters, and no middleware you
can hang a debugger off. The seams are explicit instead, and they are these:

| I want to see… | Look at |
| -------------- | ------- |
| Did this request even qualify? | `AnthropicBodyTransform.AppliesTo` — returns false → relay streamed it, nothing to debug. |
| What did the body look like? | `AnthropicMessagesRewriter.Rewrite`, `utf8Body` param. This is the raw request. |
| Which tool_results did we find, and what tool made them? | `LocateToolResults` return value: `toolNames` + `toolResults`. |
| Why was this one skipped? | The `qualifying` filter loop. Skipped = tool not in the scope allowlist. |
| **What config are we running?** | The `CompressionPlan` param. `plan.EnabledIds` is the literal stage set. |
| **What did each stage do?** | The `StageTrace[]` the pipeline appends. |
| Why did nothing change? | A trace of all `Disabled` = config. All `NoChange` = nothing to do. Any `Aborted` = a fail-open rule fired; see `OutputMinifier` remarks. |
| Why did the saving get thrown away? | `scratch.WrittenCount < token.Length` in the rewriter — re-serialization grew. |

**The trace is a contract.** Every stage in the catalog appends *exactly one*
`StageTrace` on every call — including stages that were off, and stages that weren't
applicable. A stage missing from a trace is a bug in `CompressionPipeline`, not a
stage that did nothing. This is deliberate: it means a capture shows you the **whole**
pipeline, so you can tell "that stage is off" apart from "that stage ran and
declined", which are the two answers you actually need and are otherwise
indistinguishable.

The per-stage char counts are not estimates. `MinifyStats` already attributes every
removed char to the transform that removed it inside the fused scan; the pipeline just
maps those counters onto stage ids.

### Why the fused stages are not five separate passes

`CompressionCatalog` lists stages 1–6 as six independent toggles, but they execute
as **one** forward scan in `OutputMinifier`. This looks like a leaky abstraction and
is a deliberate trade:

The idempotency proofs are **cross-transform**. The rule "with cr-collapse off, a bare
CR aborts the whole string" only makes sense if T1 and T3 can see each other. Six
genuinely independent passes would each be idempotent alone and **not** idempotent
composed — which is the exact bug class that quietly destroys prompt caching.

So: the transforms are individually **killable** (that's what the toggles give you,
and it's what makes bisecting a misbehaving transform possible). They are not
individually **schedulable**. Same story for stages 11–12 in `OutputCondenser`.

## Captures

When diagnostic capture is explicitly enabled, textual output from every recognized Codex tool
and every allowlisted tool result for the other providers is written to the `CompressionCaptures`
table in `state.db` with a **GUID**, the raw text, the compressed candidate, whether that candidate
was actually forwarded, and the exact enabled-id set active for the request. Array-form outputs
produce one row per textual block. The legacy trace column is retained but new persisted rows leave
it empty; live in-memory captures still carry a trace while an allowlisted pipeline run executes.
Codex tools outside its compression allowlist are diagnostic-only: raw and compressed text are
identical, `RewriteAccepted` is false, the trace is empty, and the original request bytes are
forwarded unchanged. Images and other non-text blocks are never captured. Capture is off by
default because these values can contain source code, paths, secrets printed by commands, and
other sensitive local content.

This is the deliberate exception to the "proxy never logs bodies" rule, and the
exception is the point: the savings tally tells you *that* 40% came off; only a raw
capture tells you whether what came off **mattered**. Captures answer "was this
compression correct", which is not a question byte counts can answer.

- **The GUID is the handle.** Paste it at an LLM reviewer, look it up in Vibe AI, cite
  it in a bug report. See [`runbooks/compress_runbook.md`](../runbooks/compress_runbook.md).
- **Grain is per textual output string, not per request.** "This Bash output compressed wrong" is
  the real grain of every bug; one request carries many tool results, and an array result can carry
  multiple text blocks that are captured separately.
- **`RawText` is re-runnable.** It is the unescaped string the rewriter observed, so feeding
  it back through a different `CompressionPlan` gives an honest what-if — which is
  exactly what `POST /api/v1/compression/preview` and the Vibe AI preview do. They call
  the real pipeline; they do not simulate it.
- **`RewriteAccepted` is the wire truth.** A changed candidate can still be rejected when JSON
  re-serialization would make its token larger. It remains captured to explain the missed saving,
  but the original text is what went upstream.
- **Uncapped, by explicit decision (2026-07-15).** No pruning, no size cap, no
  truncation. Capping would preferentially destroy the pathological inputs that are the
  only reason to look. It grows without bound;
  `DELETE /api/v1/compression/captures` is the reset.
- **The in-memory writer is bounded.** Persistence remains uncapped, but a slow/locked database
  cannot make the relay retain captures without limit; overload drops diagnostics rather than
  blocking or exhausting the proxy process.
- **Captures contain verbatim file and command output from the user's machine.** Treat
  them exactly like `SessionLogs`: never checked in, never shipped off-box outside the
  encrypted debug bundle.

## Settings

**On/off per LLM is the whole user-facing surface** (2026-07-18): one switch each for
Claude, Codex, and OpenCode in the settings UI. When a provider's saver is on (and
that provider's proxy is on), the pipeline runs `CompressionCatalog.DefaultSelection`
— the curated set in the table above. The per-stage picker UI is gone.

- `ClaudeTokenSaverEnabled` / `CodexTokenSaverEnabled` / `OpenCodeTokenSaverEnabled`
  in `settings.json` are the three switches. The Codex/OpenCode keys are **nullable**:
  a pre-split file has only `ClaudeTokenSaverEnabled` (which used to be the master
  kill switch for every provider), and an absent key inherits its value — so an old
  "everything off" choice stays off after upgrading. Saving from the UI writes
  explicit values and severs the inheritance.
- `TokenSaverStageOverride` is a hand-edit escape hatch with no UI: a non-null list of
  stage/scope ids replaces the curated set wholesale, so a misbehaving stage can be
  bisected on live traffic without turning the whole saver off. `null` (the only value
  the product itself writes) = curated defaults; `[]` = saver on but a no-op. Unknown
  ids are ignored. It is deliberately **not** the old `TokenSaverStages` key — the
  retired stage picker persisted that key on every save, and honoring it would have
  frozen early adopters on their old selection, silently exempting them from the
  curated set. The old key and the pre-2026-07 tier/bool knobs are ignored on read
  and dropped on the next save.
- `TokenSaverCaptureEnabled` independently opts into raw diagnostic captures; it defaults off.
  For Codex requests processed by the saver, this also observes recognized textual tool output
  when the tool is outside the compression allowlist; observation alone never makes that output
  eligible for rewriting.

The per-tab toggle that used to sit on each terminal tab was removed 2026-07-19 (the tab action
strip was overcrowded, and per-LLM is the granularity that matters). The three settings switches
are now the only gates.

Settings are read in **one fresh snapshot per request** (`ILlmProxySettingsService`)
so a concurrent save can't tear a single body's rewrite in half.

### Pausing — the agent's escape hatch

Every elision leaves a marker, so a model always knows something was removed. Until 2026-07-31 it
had no way to get it back short of asking the user to flip a switch. Now it can pause compression
for **5 minutes, for its own terminal tab**, re-run the command, and read the output verbatim.

Three MCP tools drive it — `pause_token_saver`, `resume_token_saver`, `get_token_saver_status`
(`VibeRails/Services/Mcp/Tools/TokenSaverTool.cs`). They call
`/llm/control/token-saver/*` (POST for pause/resume, GET for status), which sits under the same
prefix and behind the same session+tab auth gate as the proxy routes.

**The pause is held in memory by the process that hosts the proxy**
(`VibeRails/Services/LlmProxy/TokenSaverPauseState.cs`), and that is the terminal tab's own child
`vb.exe`. Everything else follows from that:

- It is **per-tab by construction** — no tab id to plumb, no shared file to key or prune. One
  agent's pause cannot disable another tab's saver.
- No SQLite on the request path, and no ephemeral runtime state written into `settings.json`
  where it would race the settings UI's read-modify-write save.
- It dies with the tab, which is the right lifetime.
- Expiry is evaluated **on read**, so nothing has to fire for compression to come back. There is no
  timer to drop and no way to get stuck paused.

It is applied in exactly one place: `LlmProxySettingsService.Resolve` forces the three
`*TokenSaverEnabled` fields false while paused, so **every provider — and any provider added
later — inherits the pause for free**, and this library needed no changes at all. The
`*LlmProxyEnabled` flags are deliberately untouched: a pause makes requests pass through
unrewritten, it does not 404 a CLI mid-conversation. The resolved plan is untouched too, so a
capture taken during a pause still reports the configured stage set rather than looking like a
saver configured to do nothing.

**A pause costs two prompt-cache breaks, and that is the reason it is short and fixed.** The CLI
re-sends its whole history every turn. Turning compression off rewrites the bytes of
`tool_result`s that already went up compressed, which breaks the provider's cache at the first one
and reprocesses everything after it at full price — once when the pause starts and again when it
ends. That is a fair trade for a deliberate "I need to see this output" moment and a bad one for a
speculative toggle, which is why the tool description tells the agent not to call it on a hunch,
why there is no `minutes` argument to inflate, and why the window expires by itself.

The dashboard's piggy-bank meter shows a `Paused m:ss` badge while any tab is paused, fed by the
`token_saver_pause` app event the control route publishes. The browser counts down from the
absolute expiry it was given, so the badge clears itself on time without polling. Known gap: a
browser reload mid-pause loses the badge until the next pause or resume — the pause itself is
unaffected and still expires on schedule.

## Project layout

```
TokenSaver/
├─ Pipeline/
│  ├─ CompressionCatalog.cs      ← START HERE. Stages, scopes, defaults, order.
│  ├─ CompressionPipeline.cs     ← The compression path. Breakpoint target.
│  ├─ CompressionPlan.cs         ← A resolved selection (flags + shapes + allowlists).
│  └─ CompressionTrace.cs        ← StageTrace / StageOutcome.
├─ Minify/
│  ├─ OutputMinifier.cs          ← Stages 1-6, fused. Read the class comment.
│  ├─ OutputCondenser.cs         ← Stages 11-12, fused. Read the class comment.
│  ├─ MinifyFlags.cs             ← 6 bools ← catalog stages 1-6.
│  ├─ CondenseOptions.cs         ← 2 bools ← catalog stages 11-12.
│  ├─ MinifyStats.cs             ← Per-transform char counters (= the trace source).
│  ├─ CondenseStats.cs
│  ├─ AnthropicMessagesRewriter.cs  ← JSON walk + splice (Claude).
│  ├─ AnthropicBodyTransform.cs     ← Buffering + fail-open (Claude).
│  ├─ CodexResponsesRewriter.cs     ← JSON walk + splice (Codex).
│  ├─ CodexBodyTransform.cs
│  ├─ ChatCompletionsRewriter.cs    ← JSON walk + splice (zai/Z.AI + xai/Grok via OpenCode).
│  ├─ ZaiBodyTransform.cs           ← Buffering + fail-open (OpenCode Chat Completions).
│  ├─ PooledBufferWriter.cs      ← Plumbing.
│  ├─ PrefixedBodyStream.cs      ← Plumbing (the over-cap stitch-back).
│  └─ ToolOutputRewriteResult.cs
├─ Shape/
│  ├─ CommandShape.cs            ← Classifies a COMMAND (not output) into a shape.
│  └─ ShapeFilters.cs            ← Stages 7-10.
├─ LlmProxyRelay.cs              ← The generic forwarding relay.
├─ LlmProxyRoutes.cs             ← Codex proxy endpoints.
├─ LlmAnthropicProxyRoutes.cs    ← Claude proxy endpoints.
├─ LlmZaiProxyRoutes.cs          ← zai/Z.AI (GLM) proxy endpoints (via OpenCode).
├─ LlmXaiProxyRoutes.cs          ← xai (Grok) proxy endpoints (via OpenCode).
├─ ILlmProxySettingsService.cs   ← Settings seam (impl lives in VibeRails).
├─ ILlmProxyEventSink.cs         ← Telemetry seam. Counts only, never content.
├─ ILlmProxyExchangeSink.cs      ← Whole-request/response exchange log seam.
├─ ICompressionCaptureSink.cs    ← Capture seam. Content, deliberately.
├─ ILlmProxyAuthGate.cs
├─ ILlmProxyBodyTransform.cs
└─ LlmProxyBaseUrl.cs / LlmProxyClaudeConfig.cs / LlmProxyCodexConfig.cs / LlmProxyZaiConfig.cs / LlmProxyXaiConfig.cs
```

Host-side implementations live in `VibeRails/`:

- `VibeRails/Services/LlmProxy/` — the settings, event-sink, capture-sink and exchange-sink adapters, and the in-memory pause state (`TokenSaverPauseState.cs`).
- `VibeRails/DB/CompressionCaptureStore.cs` — the per-tool_result capture writer.
- `VibeRails/DB/LlmExchangeLogStore.cs` — the whole-request/response log. Its own database file
  (`~/.vibe_rails/proxy_exchanges.db`), never state.db. Every authenticated exchange handled by
  any proxy route is logged; there is no settings flag or UI toggle. This is the artifact to reach
  for when judging where compression could go next, because unlike a stage trace it stays valid
  after the stages change.
- `VibeRails/DB/TokenSavingsStore.cs` — the byte tally.
- `VibeRails/Routes/CompressionCaptureRoutes.cs` — captures, catalog, preview.
- `VibeRails/Routes/TokenSaverPauseRoutes.cs` — the pause/resume/status control surface (see [Pausing](#pausing--the-agents-escape-hatch)).

## Tests

```powershell
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~TokenSaver"
```

The suite that matters most is the **property tests**. `OutputMinifier` has a 32-combo
test that runs every flag combination over a corpus and asserts deletion-only,
idempotency, and determinism. If you add a stage, it needs the same treatment — a
stage without an idempotency property test is a prompt-cache bug waiting to happen.

Golden fixtures live under `Tests/TokenSaver/`. **New fixture directories need
`-text` in `.gitattributes` BEFORE the first commit** — `text=auto` will otherwise
smudge byte-exact fixtures on a fresh Windows checkout and you'll get failures while
`git status` shows clean.

## Things that will bite you

- **`token_saving_plan.md` does not exist, and nothing points at it any more.** It was
  deleted; the comments that cited its `§2`/`§3`/`§5`/`§6` were rewritten (2026-07-15) to
  state the rule they rely on, or to point here. The invariants were always real — only
  the pointer was dead. If you find a fresh citation to it, it came from an old branch.
- **Char counts ≠ wire bytes.** `MinifyStats` and `StageTrace` count chars; one
  stripped ESC is 6 wire bytes after JSON escaping, one stripped space is 1. Use char
  counts to compare stages; use the rewriter's byte counts to quote savings.
- **A shorter string can serialize larger** (surrogate escaping). Hence the wire-level
  size check.
- **`ClaudeTokenSaverEnabled` is still the inherited default for Codex and OpenCode**
  when their nullable keys are absent from settings.json (pre-split files). Once the
  UI saves, each provider stands on its own value. See [Settings](#settings).
- **Shape filters key off the command, never off output sniffing.** `CommandShape.Classify`
  refuses anything containing a pipe, redirect, `&&`, `;`, or `$(` — if we can't see
  the whole command, we don't know the output's shape, and guessing means mangling
  output we didn't understand.
- **On Windows-native runners the shape stages silently no-op on `dotnet test`.**
  `ShapeFilters.Apply` fail-opens on *any* CR (`IndexOfAny('\x1b', '\a', '\r')`), and
  `cr-collapse` — the only stage that normalizes CRLF→LF — is **off** by default. So a
  `dotnet test` (or any) payload with CRLF line endings used to reach the shape stage still
  carrying `\r`, so `elide-passed-tests`, the three group stages, `dedupe-lines` and
  `truncate-long` all returned it untouched — six of eleven stages dead on every Windows payload,
  reporting `NoChange` rather than `Aborted` so it never looked like a failure. Fixed 2026-07-28
  by splitting CRLF→LF out of `cr-collapse` into the on-by-default `crlf-normalize` stage. The
  fail-open itself is unchanged and still correct: it now fires only on a genuine bare CR (a
  redraw frame), which is what it was written for. `cr-collapse` remains off.
- **The relay must never wait on SQLite.** `TokenSavingsStore` persists in the background and
  `CompressionCaptureStore` enqueues work to its single ordered consumer; both set `busy_timeout`
  and swallow write failures. `state.db` has known lock contention; a lost capture is a bad
  afternoon, a blocked relay is a broken product.
- **No single process knows what the app has saved.** The proxy runs wherever the CLI runs, and
  that is the terminal tab's own child `vb.exe` — never the root backend that serves the
  dashboard. So a savings number held in one process's memory describes one tab: the root's would
  sit at zero forever, and a tab's restarts at zero every time that tab is spawned. Anything the
  UI displays has to be re-read from the `TokenSavings` table (`ITokenSavingsStore.RefreshAsync`),
  which is why `GET /api/v1/token-savings` refreshes before answering and why the root rewrites
  the tallies on a `proxy_activity` ping it relays out of a child
  (`TerminalTabHostService.EnrichPayload`). "This session" is a delta — what the table has gained
  since this process started — not a counter of this process's own requests.

---

*Last checked: 2026-08-06T16:53:19Z by opencode (glm-5.2)*
