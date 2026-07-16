# TokenSaver

TokenSaver is the library that sits between a coding CLI (Claude Code, Codex) and its
model provider, and makes the request smaller on the way up.

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
| 3 | **`Minify/OutputMinifier.cs`** | Stages 1–5, fused into one forward scan. The lossless pass. Its class comment is the real spec — read it before touching a byte. |
| 4 | **`Shape/ShapeFilters.cs`** | Stages 6–8. Regroups the output of a *recognised command* (`git status --short`, `rg -n`, `find`). |
| 5 | **`Minify/OutputCondenser.cs`** | Stages 9–10, fused. The lossy pass — dedupe runs, elide long middles. |

And the two files that decide **which strings ever reach the pipeline**:

| File | What it is |
| ---- | ---------- |
| `Minify/AnthropicMessagesRewriter.cs` | Walks the Anthropic JSON body, finds `tool_result` blocks, matches each to the `tool_use` that produced it (for the tool name **and the command text**), and splices rewritten strings back in. |
| `Minify/CodexResponsesRewriter.cs` | Same job for Codex's `/responses` shape. |

Everything else in this project is plumbing: relays, buffers, auth gates, settings
seams. If you are debugging *compression*, it is one of the seven files above.

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
             1-5 OutputMinifier (fused)  |  lossless
             6-8 ShapeFilters            |  reshaping
             9-10 OutputCondenser (fused)|  lossy
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
| 1 | `cr-collapse` | Lossless | **off** | Keeps the final frame of a `\r`-redrawn line, only when it provably covers earlier frames. Normalizes CRLF→LF. |
| 2 | `ansi-strip` | Lossless | **off** | Drops SGR colour, OSC titles, BEL. Cursor moves/erases kept verbatim. |
| 3 | `trailing-whitespace` | Lossless | on | Spaces/tabs at end of line. |
| 4 | `blank-edges` | Lossless | on | Blank lines at the start/end of the payload. |
| 5 | `blank-runs` | Lossless | on | Runs of 3+ blank lines → 2. |
| 6 | `git-status-group` | Reshaping | on | Groups porcelain `XY path` lines under one header per status. |
| 7 | `grep-group` | Reshaping | on | Groups `path:line:content` under one header per file. |
| 8 | `find-group` | Reshaping | on | Groups a flat path list under one header per directory. |
| 9 | `dedupe-lines` | Lossy | **off** | 3+ identical lines → one, tagged `[xN]`. |
| 10 | `truncate-long` | Lossy | **off** | Keeps first 150 + last 50 lines, elides the middle. |

**Why are `cr-collapse` and `ansi-strip` off by default?** They are the two largest
lossless wins available, and they are off by deliberate product decision (2026-07-15):
they reshape terminal-looking output, and the owner wants to opt into that
consciously rather than inherit it. This is a preference, not a safety finding. Do
not "fix" it by flipping the defaults.

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

`CompressionCatalog` lists stages 1–5 as five independent toggles, but they execute
as **one** forward scan in `OutputMinifier`. This looks like a leaky abstraction and
is a deliberate trade:

The idempotency proofs are **cross-transform**. The rule "with cr-collapse off, a bare
CR aborts the whole string" only makes sense if T1 and T3 can see each other. Five
genuinely independent passes would each be idempotent alone and **not** idempotent
composed — which is the exact bug class that quietly destroys prompt caching.

So: the transforms are individually **killable** (that's what the toggles give you,
and it's what makes bisecting a misbehaving transform possible). They are not
individually **schedulable**. Same story for stages 9–10 in `OutputCondenser`.

## Captures

When diagnostic capture is explicitly enabled, every allowlisted tool result the pipeline
considers is written to the `CompressionCaptures` table in `state.db` with a **GUID**, the raw
text, the compressed candidate, whether that candidate was actually forwarded, the stage trace,
and the exact enabled-id set it ran under. Capture is off by default because these values can
contain source code, paths, secrets printed by commands, and other sensitive local content.

This is the deliberate exception to the "proxy never logs bodies" rule, and the
exception is the point: the savings tally tells you *that* 40% came off; only a raw
capture tells you whether what came off **mattered**. Captures answer "was this
compression correct", which is not a question byte counts can answer.

- **The GUID is the handle.** Paste it at an LLM reviewer, look it up in Vibe AI, cite
  it in a bug report. See [`runbooks/compress_runbook.md`](../runbooks/compress_runbook.md).
- **Grain is per tool_result, not per request.** "This Bash output compressed wrong" is
  the real grain of every bug; one request carries many tool_results.
- **`RawText` is re-runnable.** It is the unescaped string the pipeline saw, so feeding
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

One knob: `TokenSaverStages` in `settings.json` — a list of enabled stage/scope ids.

- `null` = never configured → `CompressionCatalog.DefaultSelection`.
- `[]` = a real choice: everything off. **Not** the same as `null`.
- Unknown ids are ignored, so a settings.json from a newer build degrades to "that
  stage is off here" rather than failing a request.
- `ClaudeTokenSaverEnabled` is the master kill switch for **both** providers, despite
  the name. A provider's saver only runs if that provider's proxy is also on.
- Every Web UI terminal tab also has a process-local zip toggle. It is on by default and acts as
  an additional gate over the global switch: turning it off bypasses compression for that tab's
  Claude and Codex proxy requests without changing sibling tabs or restarting the CLI.
- `TokenSaverCaptureEnabled` independently opts into raw diagnostic captures; it defaults off.

This replaced the old `TokenSaverLevel` tier string (`off`/`safest`/…/`high`) and its
five per-transform bools in 2026-07. A tier can only express the combinations we
thought of in advance, and the entire premise of this feature is that we are still
learning which combinations are good.

Settings are read in **one fresh snapshot per request** (`ILlmProxySettingsService`)
so a concurrent save can't tear a single body's rewrite in half.

## Project layout

```
TokenSaver/
├─ Pipeline/
│  ├─ CompressionCatalog.cs      ← START HERE. Stages, scopes, defaults, order.
│  ├─ CompressionPipeline.cs     ← The compression path. Breakpoint target.
│  ├─ CompressionPlan.cs         ← A resolved selection (flags + shapes + allowlists).
│  └─ CompressionTrace.cs        ← StageTrace / StageOutcome.
├─ Minify/
│  ├─ OutputMinifier.cs          ← Stages 1-5, fused. Read the class comment.
│  ├─ OutputCondenser.cs         ← Stages 9-10, fused. Read the class comment.
│  ├─ MinifyFlags.cs             ← 5 bools ← catalog stages 1-5.
│  ├─ CondenseOptions.cs         ← 2 bools ← catalog stages 9-10.
│  ├─ MinifyStats.cs             ← Per-transform char counters (= the trace source).
│  ├─ CondenseStats.cs
│  ├─ AnthropicMessagesRewriter.cs  ← JSON walk + splice (Claude).
│  ├─ AnthropicBodyTransform.cs     ← Buffering + fail-open (Claude).
│  ├─ CodexResponsesRewriter.cs     ← JSON walk + splice (Codex).
│  ├─ CodexBodyTransform.cs
│  ├─ PooledBufferWriter.cs      ← Plumbing.
│  ├─ PrefixedBodyStream.cs      ← Plumbing (the over-cap stitch-back).
│  └─ ToolOutputRewriteResult.cs
├─ Shape/
│  ├─ CommandShape.cs            ← Classifies a COMMAND (not output) into a shape.
│  └─ ShapeFilters.cs            ← Stages 6-8.
├─ LlmProxyRelay.cs              ← The generic forwarding relay.
├─ LlmProxyRoutes.cs             ← Codex proxy endpoints.
├─ LlmAnthropicProxyRoutes.cs    ← Claude proxy endpoints.
├─ ILlmProxySettingsService.cs   ← Settings seam (impl lives in VibeRails).
├─ ILlmProxyEventSink.cs         ← Telemetry seam. Counts only, never content.
├─ ICompressionCaptureSink.cs    ← Capture seam. Content, deliberately.
├─ ILlmProxyAuthGate.cs
├─ ILlmProxyBodyTransform.cs
└─ LlmProxyBaseUrl.cs / LlmProxyClaudeConfig.cs / LlmProxyCodexConfig.cs
```

Host-side implementations live in `VibeRails/`:

- `VibeRails/Services/LlmProxy/` — the settings, event-sink and capture-sink adapters.
- `VibeRails/DB/CompressionCaptureStore.cs` — the capture writer.
- `VibeRails/DB/TokenSavingsStore.cs` — the byte tally.
- `VibeRails/Routes/CompressionCaptureRoutes.cs` — captures, catalog, preview.

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
- **`ClaudeTokenSaverEnabled` controls Codex too.** Legacy name.
- **Shape filters key off the command, never off output sniffing.** `CommandShape.Classify`
  refuses anything containing a pipe, redirect, `&&`, `;`, or `$(` — if we can't see
  the whole command, we don't know the output's shape, and guessing means mangling
  output we didn't understand.
- **The relay must never wait on SQLite.** `TokenSavingsStore` persists in the background and
  `CompressionCaptureStore` enqueues work to its single ordered consumer; both set `busy_timeout`
  and swallow write failures. `state.db` has known lock contention; a lost capture is a bad
  afternoon, a blocked relay is a broken product.
