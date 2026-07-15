# Compression runbook — judging a capture

**Purpose.** This is the jump-off for using an LLM (usually Claude, in this repo) as a
judge of the token saver's output. You found something that looks wrong. You have a
capture GUID. You point an agent at this file and that GUID, and it comes back with a
verdict.

**The one-line invocation:**

> Read `runbooks/compress_runbook.md` and judge capture `a3f1c8e2-…`.

Everything below is written for the agent that gets that message.

---

## 0. What a capture is

When diagnostic capture is enabled, every allowlisted `tool_result` string the token saver
*considers* — rewritten or not — is offered to the background writer for the
`CompressionCaptures` table in `~/.vibe_rails/state.db`. Capture is best-effort: the bounded
in-memory queue drops diagnostics under sustained database backpressure rather than blocking the
proxy relay.

A capture holds:

| Field | Meaning |
| ----- | ------- |
| `Id` | The GUID. The handle for everything. |
| `Provider` | `anthropic` / `openai`. |
| `ToolName` | `Bash`, `PowerShell`, `Read`, … The tool whose output this is. |
| `Command` | The shell command, when we could see it. **`NULL` is meaningful** — see §4. |
| `RawText` | The unescaped string the pipeline saw. Re-runnable. |
| `CompressedText` | The pipeline's candidate output. It was forwarded only when `RewriteAccepted` is true. |
| `RewriteAccepted` | Whether that candidate was actually forwarded. `false` with changed text means the wire-size guard rejected it and sent `RawText`. |
| `Trace` | One entry per catalog stage: `{stageId, outcome, charsRemoved}`. |
| `EnabledIds` | The exact stage/scope set this ran under. |

Background: [`TokenSaver/README.md`](../TokenSaver/README.md). Read it first if you
have not — you cannot judge a stage without knowing its invariants.

## 1. Fetching the capture

Preferred, if `vb.exe` is running:

```powershell
curl.exe -s -H "X-Api-Key: $env:VIBE_API_KEY" http://localhost:5000/api/v1/compression/captures/<GUID>
```

Direct, always works:

```sql
-- sqlite3 ~/.vibe_rails/state.db
SELECT Provider, ToolName, Command, CharsBefore, CharsAfter, Changed,
       RewriteAccepted, EnabledIds
FROM CompressionCaptures WHERE Id = '<GUID>';

SELECT RawText FROM CompressionCaptures WHERE Id = '<GUID>';
SELECT CompressedText FROM CompressionCaptures WHERE Id = '<GUID>';
SELECT Trace FROM CompressionCaptures WHERE Id = '<GUID>';
```

Copy state.db to a temp file before poking it. `vb.exe` holds it open and this DB has
known lock contention — see the note in `terminal/SESSION_DEBUG_PLAYBOOK.md`.

Recent captures, when you have a symptom but no GUID:

```sql
SELECT Id, CreatedUTC, ToolName, substr(Command,1,60), CharsBefore, CharsAfter,
       Changed, RewriteAccepted
FROM CompressionCaptures ORDER BY CreatedUTC DESC LIMIT 40;
```

## 2. The verdict you must return

Exactly one of these. Do not hedge; if you cannot decide, say **INSUFFICIENT** and
say what you'd need.

| Verdict | Means |
| ------- | ----- |
| **CORRECT** | The compression preserved everything a model needs to act on this output. |
| **LOSSY-BUT-INTENDED** | Information was destroyed, but by a `Lossy` stage doing exactly its job, and the marker makes the loss visible to the model. |
| **WRONG** | The model would behave differently, or worse, because of what we did. |
| **INSUFFICIENT** | The capture doesn't contain enough to tell. Say what's missing. |

## 3. The rubric

Work these in order. Stop at the first **WRONG**.

### 3.1 Did we destroy something a model would act on?

First check `RewriteAccepted`. If it is false, the model received `RawText`, not
`CompressedText`; the changed text is a rejected diagnostic candidate and did not alter that
request. You may still judge the candidate to find a pipeline defect, but do not describe its
differences as forwarded output.

For an accepted rewrite, diff `RawText` against `CompressedText`. For every difference, ask:
*would a model reading only the compressed version make a different decision?*

Specific things that make it **WRONG**:

- A file path, line number, error code, hash, or identifier is gone or altered.
- A stack frame or the first error in a run is inside an elided middle.
- Output that is exact-match material for a later step (file contents, an `old_string`
  candidate) was altered **at all**. See §5.
- A `[xN]` marker collapsed lines that were *not* actually identical.

**Not** wrong on its own: colour codes gone, trailing spaces gone, blank lines
collapsed, a progress bar reduced to its final frame. Those are the job.

### 3.2 Does the trace explain the diff?

Every difference between raw and compressed must be attributable to a stage in the
trace that is marked `Applied`.

**A diff that no `Applied` stage accounts for is a bug — report it immediately**, with
the GUID. That means a transform did something outside its declared contract, and it's
the most serious class of finding here.

Read the trace as a whole:

- All `Disabled` → config, not code. The user turned things off. Not a bug.
- All `NoChange` → stages ran and declined. Working as designed.
- Any `Aborted` → a fail-open rule fired (malformed escape, control char). **This is a
  designed outcome, not an error.** See `OutputMinifier`'s class comment. Only
  interesting if abort rate is high.
- A stage **missing** from the trace → bug in `CompressionPipeline`. Every catalog
  stage appends exactly one entry on every call. Report it.

### 3.3 Would a re-run drift? (the cache question)

The killer bug in this library is not lost meaning, it's **non-determinism**. The CLI
re-sends the whole history every turn; if a string compresses differently on turn 9
than turn 4, the provider's prompt cache breaks at turn 3 and everything after it
re-prices at full rate. A drifting transform costs *more* than doing nothing.

Check idempotency directly:

```
POST /api/v1/compression/preview  { captureId: "<GUID>", enabledIds: [<the capture's EnabledIds>] }
```

When `scopeAllowed` is true, the `output` candidate must be **text-identical** to the stored
`CompressedText`. Then feed that output through the pipeline again — it must not change. If either
fails, that is **WRONG** and it is the highest-severity finding in this document. Escalate
immediately. If `scopeAllowed` is false, the preview correctly returns raw text because the real
proxy would not invoke the pipeline for that tool under the selected scopes.

The preview operates on unescaped strings. It does not reconstruct the original JSON token or run
the rewriter's serialized wire-size guard, so its output is only a pipeline candidate; use the
capture's `RewriteAccepted` value to determine what the captured request actually forwarded.

### 3.4 Was it worth it?

`CharsBefore` vs `CharsAfter`. A `Reshaping` stage that saved <2% but restructured the
output is a bad trade — reshaping costs model comprehension. Say so; that's a design
finding, not a bug.

## 4. Reading the `Command` field

`Command` is how shape filters (`git-status-group`, `grep-group`, `find-group`) decide
to fire. `CommandShape.Classify` is deliberately paranoid: it returns `None` for
anything with a pipe, redirect, `&&`, `;`, or `$(`, because if we cannot see the whole
command we do not know the shape of its output, and guessing means mangling output we
did not understand.

So:

- `Command` is `NULL` or unclassified + a shape stage says `NotApplicable` → **correct
  behaviour**, not a miss. Don't file it as a bug.
- A shape stage fired on output that isn't its shape → **WRONG**, and it means
  `Classify` has a hole. This is the highest-value bug class in the shape filters.

## 5. The two stages to be suspicious of

`scope-read` and `scope-grep` ship **off**. If a capture has `ToolName = Read`,
`Changed = 1`, and `RewriteAccepted = 1`, someone turned `scope-read` on and the changed output was
forwarded. With `RewriteAccepted = 0`, the stored difference is only a rejected candidate.

For these, the failure mode is not "the model is confused", it's **"a later Edit
fails"**: the model builds Edit `old_string` values out of Read output verbatim. Any
change to Read output — even stripping a trailing space — can make an `old_string` not
match. Judge Read captures against a much harsher bar than shell output: *any* change
to file content is **WRONG** unless you can show the model never quotes it.

If you're judging a batch to decide whether `scope-read` is safe to enable, that is the
question to answer, and the answer needs to be "no capture in N was altered in a way
that could break an exact-match edit" — not "the compression looked reasonable".

## 6. What to report

```
Capture:   <GUID>
Verdict:   CORRECT | LOSSY-BUT-INTENDED | WRONG | INSUFFICIENT
Stage:     <the stage id responsible, or "none">
Evidence:  <the specific diff, quoted — raw on one side, compressed on the other>
Impact:    <what the model would do differently. Be concrete.>
Fix:       <file:line + what to change, or "config: turn off <stage>">
```

Rules for the report:

- **Quote the actual bytes.** A verdict without a quoted diff is an opinion.
- **Name the stage.** "The compression broke it" is not actionable; `grep-group` is.
- **Separate config from code.** "This stage is on and you don't want it" is a settings
  change. "This stage is on and does the wrong thing" is a code change. They have very
  different fixes and conflating them wastes a debugging session.
- **Do not propose relaxing a fail-open rule.** Every abort rule in `OutputMinifier`
  and `OutputCondenser` closes a specific idempotency hole, each documented at the
  point of the rule. If a rule seems over-cautious, the cost is lost savings on one
  string; the cost of removing it is a cache-thrash bug that is nearly impossible to
  diagnose from its symptom (a session that is mysteriously slow and expensive).
  Propose it only with a proof, and expect the proof to be wrong.

## 7. Batch judging

To answer "is stage X safe to turn on?", don't reason about it — measure it:

```sql
SELECT Id FROM CompressionCaptures
WHERE ToolName = 'Bash' AND Changed = 1 AND RewriteAccepted = 1
ORDER BY CreatedUTC DESC LIMIT 50;
```

For each: `POST /api/v1/compression/preview` with `enabledIds` = current + X, diff the
result against `CompressedText`, and judge only the deltas. That isolates X's contribution from
everything else, which is the only way to get a clean answer — and it's the entire reason
`RawText` is stored re-runnable rather than as a rendered diff. The preview result is a candidate,
not proof that the rewriter would accept it on the JSON wire; validate wire acceptance separately
before claiming that a changed preview would be forwarded.

Report: N captures, M changed by X, K judged WRONG, and the worst case quoted.

## 8. History

- **2026-07-15** — Runbook created alongside the stage-catalog rework. The old
  `TokenSaverLevel` tiers (off/safest/safe/medium/high) were replaced by independently
  toggleable stages; `cr-collapse` and `ansi-strip` were moved OFF by default at the
  owner's request (preference about terminal-shaped output, not a safety finding);
  `scope-read`/`scope-grep` added as off-by-default toggles so their safety could be
  settled with captures instead of argument. Persisted captures are uncapped by explicit
  decision, while the best-effort in-memory writer is bounded so database backpressure cannot
  block or exhaust the relay — see `Config.TokenSaverCaptureEnabled`.
