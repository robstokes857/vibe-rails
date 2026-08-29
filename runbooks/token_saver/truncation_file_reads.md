# truncate-long was cutting holes in source files — findings and fix

**Status: IMPLEMENTED, tests green (2196/2196), live soak owed.** Rob decides what ships.

**Started from a different question.** The dashboard's piggy-bank popover read *"This session:
2.2M tokens saved"* and the worry was the opposite of a bug report: if we really removed two
million tokens, are we starving the agent? Chasing the number found (a) the number is inflated as
a measure of content removed, (b) Claude is barely touched in aggregate, and (c) one real hole
that the aggregate was hiding. This document is the evidence trail and the fix.

Companion reading: [`compress_runbook.md`](compress_runbook.md) for judging a capture,
[`../../TokenSaver/README.md`](../../TokenSaver/README.md) for the stage catalog and invariants.

---

## 1. Where "2 million" comes from

Two separate inflations, neither of them a compression bug.

**It is Codex, not Claude.** The popover's *This session* row is a whole-table delta since the
dashboard process started — every tab, every provider. On the day in question `TokenSavings` held
`openai` 2,179,272 tokens against `anthropic` 25,047. The 2.2M was essentially all Codex.

**It counts the same bytes once per turn.** `ILlmProxyEventSink.SavingsMeasured` fires once per
HTTP request and records `BytesBefore - BytesAfter` for the *whole request body*
(`VibeRails/Services/LlmProxy/LlmProxyEventSinkAdapter.cs`). Every CLI re-sends its entire
conversation history each turn, and the pipeline is deterministic *by design* (that determinism is
what protects prompt caching), so an elision made on turn 5 is re-counted on turns 6, 7, 8… The
inflation is roughly half the turn count.

You can watch it in one conversation — the same 286 chars "saved" turn after turn, creeping to 443
over 25 turns. Tally ≈ 6.5 KB; content actually removed ≈ 443 bytes.

De-duplicated by tool-output hash across `~/.vibe_rails/proxy_exchanges.db`:

| window | tally (what the meter counts) | unique chars actually removed | inflation |
| --- | --- | --- | --- |
| Claude, 2026-08-28 | 3,337,920 (~834K tok) | **264,898 (~66K tok)** | **12.6×** |
| Codex, 2026-08-28→29 | 51,728,364 (~12.9M tok) | **1,299,384 (~325K tok)** | **39.8×** |

**The meter is not lying** — it is an honest count of bytes not sent upstream, summed over
requests, and that is a real wire/billing quantity. It is simply not the quantity you want when
the question is "how much context did the model lose". Nothing here is proposed as a change to the
meter; it is written down so the next person who reads 2M does not re-derive it from scratch.

## 2. Claude was barely being touched — in aggregate

Per-request share of the request body removed, `anthropic`, requests > 10 KB:

- all-time: **0.44%**
- 2026-08-27→29: **1.18% average**, worst single request 13.8%, and **47% of requests saved zero**
- of 264,898 unique chars removed on 2026-08-28: 21,560 were lossless whitespace/CRLF across 376
  outputs — the other 243,338 came from just **33 lossy elisions**

Codex is a different story (21.9% average) because `plan_1A` allowlisted code-mode `exec`, which
is working as intended.

So the headline answer was "no, we are not gutting Claude's context". The interesting part is what
the average was hiding.

## 3. The actual bug

`scope-read` ships **off** for a stated reason: the model builds Edit `old_string` values out of
Read output, so rewriting file contents makes edits fail. That is a correctness argument, and it is
right.

But `scope-shell` is **on**, and `cat` / `sed -n` / `Get-Content` through a shell tool *is a file
read*. `truncate-long` counts lines; it has no idea what produced them. So it treated multi-file
source dumps as log spew and replaced the middle with a marker.

**27 of the 33 lossy Claude elisions on 2026-08-28 were file reads — 173,248 chars, i.e. 71% of
everything removed from Claude that day.** Representative, with paths generalized:

```
-20,734 chars  417 lines elided  cat styles.css && cat -n App.jsx
-14,062 chars  285 lines elided  for f in db/models/*.py; do echo "=== $f ==="; cat -n "$f"; done
-12,405 chars  254 lines elided  cat -n services/ingestion.py; cat -n services/event_types.py
```

Codex was worse: 35 file-read elisions, 482,985 chars, including 967 lines out of a `git show` and
746 out of a `Get-Content` batch.

This is not a new risk class — `TokenSaver/README.md` already names it ("its output can quote file
contents the model later edits against — the same accepted risk class as `cat` through
`shell_command`"). What was missing was any measurement of how often it fires. It fires constantly,
because a multi-file `cat` is the most common way an agent loads source into context.

It degrades **visibly** — the `[... 254 lines elided ...]` marker is there, and `pause_token_saver`
exists — so this was the designed mitigation working, not silent corruption. It just relies on the
agent noticing, and a 1% average gives no hint that the removal is concentrated in exactly the
payload class the design calls dangerous.

> Live demonstration, unplanned: while reading `TokenSaver/Shape/CommandShape.cs` during this very
> investigation, `cat`-ing the file through Bash returned it with `[... 233 lines elided ...]` in
> the middle. The bug bit the session that was diagnosing it.

## 4. The fix

### Why `CommandShape.Classify` could not be reused

Every real offender is a compound command — `cat a.py; echo ===; cat b.py`,
`for f in *.py; do cat -n "$f"; done`, `$x = Get-Content a.py` — and Classify's whole-command
metacharacter rule returns `None` for all of them. That rule is correct for Classify, whose answer
*authorises a rewrite*: a wrong shape mangles output, so every rule fails toward `None`.

A suppression signal has the **opposite safety polarity**. It authorises nothing; a true answer
only makes the pipeline do *less*. A false positive costs savings on one tool result; a false
negative costs 250 lines out of the middle of somebody's source file. So the new predicate is
deliberately permissive where Classify is deliberately strict, and the two must never be
"harmonised". Both doc comments say so, and `Classify_DeclinesTheCommandsReadsFileContentsCatches`
pins the divergence as intended behaviour rather than an inconsistency to clean up.

### What landed

1. **`CommandShapes.ReadsFileContents(string?)`** — a flat lexical token sweep. No shell parse, no
   command-position tracking, no flag inspection. Matches whole tokens only, so `/var/catalog/x`
   never reads as `cat`. It has to work across the three command languages the library actually
   sees, all of which Classify declines: POSIX shell, PowerShell, and the JavaScript that Codex
   code-mode wraps around a real command.
   - file dumps: `cat bat nl head tail more less type Get-Content gc sed`
   - ripgrep-as-cat: a grep-family command **plus** a match-everything pattern (`^`, `$`, `.*`,
     `^.*$`). `rg -n '^' app.jsx` is `cat -n` with extra steps and was the single largest remaining
     category (989 KB). A real search (`rg -n 'TODO' src/`) is not included and still truncates.

2. **`CondenseOptions.PreserveVerbatimFileContents`** — a per-payload *budget selector*, not a
   transform switch. Defaults to `false` so `default(CondenseOptions)` and every existing
   two-argument construction keep the original budget.

3. **`OutputCondenser`** — `TruncateInPlace` takes `keepHead`/`keepTail` instead of reading the
   constants. File reads get **1200/200** instead of 150/50.

4. **`CompressionPipeline.Run`** — takes `bool readsFileContents` as a **required** parameter.
   Required, not optional: it is the only thing standing between `truncate-long` and the middle of
   a source file, and a new call site that silently defaulted it would reopen the hole with no
   compile error.

### Why widen the budget instead of declining outright

`truncate-long`'s real job is catastrophic-payload defence. `cat` of a 50k-line generated file
still has to be capped or it blows the very context window the stage exists to protect. So file
reads get a wider budget, not an exemption.

**1200/200 is calibrated, not chosen.** Across ~8,000 proxied requests the file-read payloads being
cut ran 194–967 elided lines, i.e. 394–1167 lines total. A 1400-line budget passes every observed
case through verbatim and still caps beyond that. Re-derive from captures before changing it (§6).

### Invariants

All three still hold, and the idempotency argument is budget-*independent*: output is exactly
`keepHead + 1 + keepTail` lines, so a re-pass at the same budget computes a 1-line middle and
declines. What the proof needs is per-payload budget **stability**, and that holds because the
budget is a pure function of the producing command — which the CLI re-sends verbatim every turn,
the same property prompt caching already requires of every other stage. The stage's own on/off flag
is untouched, so the trace still reports exactly what the plan enabled (a declining file read reads
as `NoChange`, never as absent).

## 5. Result

Replayed against **261 unique elided outputs** from real captured traffic (2026-08-26 → 08-29,
4,056,231 chars removed), using the real C# predicate:

| | outputs | chars |
| --- | --- | --- |
| now preserved | **205 / 261 (79%)** | **2,960,996 (73% of all removal)** |
| still truncated | 56 | 1,095,235 |

Tests: `Tests/TokenSaver/FileReadTruncationTests.cs` (48 cases — detector, budget arithmetic,
restated invariants, pipeline wiring), full suite **2196 passed / 8 skipped / 0 failed**.

## 6. Known gaps — deliberate, measured, not fixed here

- **`git diff` / `git show` / `git log`, ~408 KB over the sample window.** A diff is a different
  payload class with a different trade: a lockfile diff is exactly what `truncate-long` *should*
  cap. Wants its own decision, not a reflex.
- **Genuine grep searches.** `rg -n -C 15 'loadAgents|showRule' wwwroot/js/` still truncates. Those
  payloads really are search results, they can span a whole tree, and `grep-group` already reshapes
  them.
- **The savings meter still counts per request.** §1. Not touched; changing it is a product
  decision about what the number is *for*.
- **No live soak yet.** Everything above is offline replay against captured bodies.

## 7. Re-running the analysis

The measurement scripts were scratch, not shipped — they are ~40 lines each and the shapes are
worth re-deriving rather than maintaining. The recipe:

1. `~/.vibe_rails/proxy_exchanges.db` (`ProxyExchanges`) holds `RequestBefore`/`RequestAfter` for
   every proxied request. Open it **read-only** (`file:…?mode=ro`) — a dev instance is usually
   holding it. Never `SELECT` the body columns without a `CreatedUTC` bound; the file is ~41 GB.
2. Walk both bodies for tool outputs. Two shapes: Anthropic `messages[].content[]` with
   `tool_use`/`tool_result`, and Responses `input[]` with `custom_tool_call` /
   `custom_tool_call_output` (Codex `output` is a list of `{type,text}` parts).
3. **De-duplicate by hash of the raw text** before totalling anything, or you re-derive the
   per-turn inflation instead of measuring content.
4. Match `[... N lines elided ...]` in the rewritten text to find lossy elisions, and pair each
   with its command via `tool_use_id` / `call_id`.
5. To score a detector change, export `{provider, command, charsRemoved}` per unique elision and
   run the real `CommandShapes.ReadsFileContents` over it from a throwaway xunit fact — do not
   reimplement the predicate in Python, or you measure the reimplementation.

Captured bodies contain real source, paths and command output. They stay local: no fixtures, no
pasting into issues, same handling as `SessionLogs`.
