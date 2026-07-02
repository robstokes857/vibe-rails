AI wrote the stuff under this. 

becuase this is C#/.net 10 let's make sure we're using spans for all the string stuff we're doing we want this to be super fast.
and it must be AOT safe.

# Token Saving Plan — VibeRails LLM Proxy

**Purpose:** shrink the tokens a vendored CLI's model ingests by wiretapping the
one wire that actually carries them — the HTTPS call from the CLI to the model —
and stripping only *provably meaningless* bytes from tool output.

**Status:** design / not started
**Last updated:** 2026-07-01
**Scope of v1:** Claude Code only. A handful of dead-safe transforms. Nothing clever.

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

---

## 1. The Litmus Test (a transform ships only if ALL are true)

- [ ] **Lossless-for-the-model** — removes only bytes with no semantic content
      (trailing whitespace, color codes, overwritten progress frames). No words,
      numbers, paths, or structure ever disappear.
- [ ] **Deterministic** — same input → same output, byte-for-byte, every time.
      (Non-determinism would thrash Anthropic prompt caching and *raise* cost.)
- [ ] **Idempotent** — running it twice equals running it once.
- [ ] **Format-agnostic-safe** — never corrupts machine-readable output
      (`--porcelain`, `--json`, `-z`, diffs/patches, file contents).
- [ ] **Pass-through by default** — anything we don't explicitly recognize as safe
      to touch is forwarded unchanged.
- [ ] **Measurable** — emits `{transform, bytes_before, bytes_after}` so we can
      prove value and catch regressions.

If a proposed feature can't check every box, it goes in §4 (Non-Goals), not v1.

---

## 2. Where We Tap In

### The wire that matters
When Claude runs `git status`, the output does **not** go through our PTY. The
`claude` process reads it over a private pipe, packs it into the API request as a
`tool_result` block, and POSTs it to `api.anthropic.com`. That request body is
**structured JSON** — the tool output is sitting right there as a string.

So we intercept at the **network boundary**, not the PTY and not the exec layer:

```
claude CLI ──HTTPS POST /v1/messages──> [ VibeRails local proxy ] ──> api.anthropic.com
                                              │
                                    rewrite tool_result strings
                                    (SSE response streamed back untouched)
```

Why this seat and not a PATH shim / LD_PRELOAD:
- **Sees the exact bytes the model sees** — no fd guessing, no ANSI reconstruction.
- **Structured** — locate `tool_result` in JSON; no fragile stdout scraping.
- **Language-agnostic & robust** — plain HTTP + JSON in C#. No syscall interposition.
- **Compounds** — tool output stays in history and is re-sent every turn; shrinking
  it once saves on *every* subsequent turn (and keeps prompt-cache prefixes smaller).

### How it clips into VibeRails (real seams)
- **New endpoint** (sibling to, NOT part of, `Routes/ProxyRoutes.cs` — that one is
  the WS relay to VibeRails-Front). Add `Routes/LlmProxyRoutes.cs`:
  `POST /llm/anthropic/{**rest}` → forward to `https://api.anthropic.com/{rest}`.
  We already do HTTP/WS proxying in-process, so the pattern is familiar.
- **Point the CLI at it.** Claude Code honors `ANTHROPIC_BASE_URL` (used for
  gateways). Set it in the per-environment env dict at the seam we already found:
  `TerminalRunner.cs:66-70` populates `preparedSession.Environment` →
  `Terminal.CreateAsync(workDir, preparedSession.Environment, ...)`. Add:
  `ANTHROPIC_BASE_URL = http://127.0.0.1:{port}/llm/anthropic`.
  (Claude appends `/v1/messages`, so our route catches `/llm/anthropic/v1/messages`.)
- **Forward auth verbatim.** Pass through `x-api-key` / `Authorization: Bearer`
  (Pro/Max OAuth), `anthropic-version`, `anthropic-beta`. Never log them.
- **Reuse existing ANSI logic.** `TerminalTextSanitizer.ToTextWithControl` already
  classifies escape sequences by `TerminalControlType` (tested). The color-strip
  transform is a *filter over its output*, not new parser code — this is what keeps
  us on the right side of the "cheap-to-be-correct" rule.

### What we touch — and only this
- Only `messages[].content[]` blocks of `type: "tool_result"`.
- Only results whose originating tool is a **shell command** (Bash). Correlate
  `tool_result.tool_use_id` → the earlier `tool_use.name`. If we can't identify the
  tool, **pass through**.
- **Never** touch: file-read results (trailing whitespace can be significant — e.g.
  two trailing spaces = a Markdown `<br>`), `text` blocks, system prompt, tool_use
  inputs, or the streamed model response.

---

## 3. v1 Allowlist — the only things we ship first

Each is an independent on/off flag with its own bytes-saved counter.

| # | Transform | Why it's safe (zero semantic loss) | Typical win |
|---|-----------|-----------------------------------|-------------|
| 1 | **Carriage-return redraw collapse** — normalize `\r\n`→`\n`; within each line keep only the text after the last bare `\r`. | `\r` means "overwrite this line." Only the final frame survives on a real terminal; we reproduce exactly what a human would see. | **Huge** on `npm install`, `pip`, `git clone`, build logs (progress bars). |
| 2 | **ANSI color/style strip (SGR only)** — drop `ESC[…m` sequences + OSC title sequences + BEL. | Color and window-title codes carry no meaning for the model; removing them never changes text or layout. Cursor-move/erase are **left alone** in v1 (stripping those safely needs full emulation → future). | Medium on any tool that force-colors (`git -c color`, `ls`, `eslint`). |
| 3 | **Trailing whitespace strip** — remove spaces/tabs at end of each line. | Trailing whitespace is never load-bearing in command output. | Small, but free. |
| 4 | **Blank-line trim** — strip leading/trailing blank lines of the whole payload. *(Optional sub-flag: collapse runs of ≥3 blank lines → 2.)* | Trimming payload edges is fully safe. Internal-run collapse is the only opinionated bit, hence its own flag and a conservative cap. | Small. |

Ordering matters: run **1 → 2 → 3 → 4** (collapse redraws, then strip escapes, then
whitespace, then blanks). All four are content-preserving, so composition is safe.

Combined, #1 and #2 are where nearly all the real savings live; #3/#4 are cleanup.

---

## 4. Explicit Non-Goals for v1 (name them so we don't backslide)

These are tempting and all fail the litmus test today:

- ❌ **Summarizing / truncating** long output ("+400 lines") — drops information.
- ❌ **Stripping git hint lines** (`(use "git add <file>…")`) — the model sometimes
  acts on them; it's semantic.
- ❌ **Touching diffs / patches** — the model applies these; a byte off = broken edit.
- ❌ **Touching file-read contents** — see the Markdown `<br>` footgun above.
- ❌ **Reformatting / re-indenting JSON or tables** — changes structure the model parses.
- ❌ **Deduping "repeated" lines** — repetition can be meaningful (counts, logs).
- ❌ **Per-command bespoke parsers** — the 20k-to-get-right trap. If a win needs one,
  it waits for an eval harness that proves it's neutral (see §7 Phase 2).

---

## 5. Wiring Checklist (concrete)

1. `Services/Llm Proxy/OutputMinifier.cs` — one **pure function**
   `string Minify(string toolStdout, MinifyFlags flags)`, plus the tool-scoping logic.
   No I/O, fully unit-testable.
2. `Routes/LlmProxyRoutes.cs` — reverse proxy:
   - Buffer the request body (finite), parse JSON, rewrite qualifying `tool_result`
     strings, forward with headers intact.
   - **Stream the SSE response back chunk-by-chunk, untouched** (never buffer it —
     the TUI needs it live).
   - If body isn't the messages endpoint / isn't JSON / parse fails → forward raw.
3. Env injection — set `ANTHROPIC_BASE_URL` in `TerminalRunner`'s
   `preparedSession.Environment` (and the tab-host path,
   `TerminalTabHostService.cs:553`).
4. **Kill switch** — a global setting + per-transform flags. Anything smells off →
   flip off. Default: #1, #2, #3 on; #4 edge-trim on, run-collapse off.
5. **Metrics** — log per-request `{transform, bytes_before, bytes_after, tool}`;
   surface a "tokens saved" tally in the dashboard.

---

## 6. Safety-critical proxy details (don't skip)

- **Only the request is rewritten. The model's response is relayed verbatim.**
  Requests aren't streamed (clean JSON rewrite); responses are SSE (pure passthrough).
- **Determinism guards the cache.** Because history (and thus tool_results) is
  re-sent each turn, our transform must be identical every time or we bust Anthropic
  prompt caching and *increase* cost. Golden-test idempotency.
- **Fail open.** Any error in parse/transform → forward the original body unchanged.
  The proxy must never be able to break a request; worst case it saves nothing.
- **Never log secrets** (auth headers, request bodies with tokens).
- **Only log the tokens we saved**
  The only thing we should log is how many tokens we saved the user... And only if counting them wont make the request/response slower.

---

## 7. Proving It's Safe → Rollout

**Phase 0 — Plumbing, zero transforms.** Ship the proxy as a pure passthrough. Confirm
Claude works through `ANTHROPIC_BASE_URL` with byte-identical behavior, auth intact
(test both API-key and Pro/Max OAuth), SSE streaming smooth. *No transforms yet.*

**Phase 1 — The allowlist, behind flags.** Turn on #1–#4. Ship a golden-corpus test:
real tool outputs → assert (a) idempotent, (b) every non-whitespace/non-ANSI byte
preserved, (c) exit-code lines intact. Watch the bytes-saved metric. Dogfood on our
own repo for a week. Any weird agent behavior → bisect by flag.

**Phase 2 — Only if data justifies it.** Anything beyond §3 requires an **eval gate**:
run a fixed task set with the transform on vs off and show *identical* task outcomes
before it's allowed near a user. No eval, no expansion.

---

## 8. Open Questions (validate in Phase 0)

- Does Pro/Max **OAuth** flow through a custom `ANTHROPIC_BASE_URL` cleanly, or does
  it pin/redirect? (If it fights us, API-key envs get the proxy first.)
- Confirm exact base-URL path join (`{base}/v1/messages`) across current Claude Code.
- Codex / Antigravity: **same pattern, different envelope** (OpenAI / Google schemas,
  their own `*_BASE_URL`). Out of scope for v1; the tool-scoping + minifier core is
  reusable once we add per-vendor "where's the tool output" adapters.

---

### One-line summary
Sit on the CLI→model HTTPS call, rewrite only shell-tool `tool_result` strings, and
only remove bytes a terminal would have thrown away anyway. Ship four boring, provable
transforms; refuse everything that needs cleverness to be safe.
