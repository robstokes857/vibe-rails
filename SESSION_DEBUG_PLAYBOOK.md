# Session Display-Bug Playbook

When a session's UI display is wrong (cleaned text shows noise, prompt got merged with dropdown, transcript page renders garbage, xterm.js replay double-prints, etc.), follow this loop. Don't speculate from the report — pull the raw bytes first.

## Where the data lives

`C:\Users\robst\.vibe_rails\state.db` (SQLite). Relevant tables:

| Table | What it holds |
|---|---|
| `Sessions` | Session metadata: `Id`, `Cli`, `StartedUTC`, `EndedUTC`, `WorkingDirectory` |
| `SessionLogs` | Raw PTY output. `Content BLOB`, one row per chunk. **The byte arrays.** |
| `UserInputs` | Raw user submissions: `Id`, `Sequence`, `InputText`, `TimestampUTC` |
| `CleanedUserInput` | ETL-cleaned `CleanedText` shown in the UI; 1:1 with `UserInputs` via `UserInputId` |
| `TerminalSessionLogs` | Enriched replay data for xterm.js viewer (`Cols`, `Rows`, `IsAlternateScreen`, `Data BLOB`); separate from `SessionLogs` |

**Data safety:** the DB, decoded text exports, and raw session captures can contain real prompts, paths, terminal output, secrets, or customer/project data. Use them locally to diagnose the bug, but do **not** check in the actual session used to write the unit test. Only commit minimized, sanitized regression fixtures that contain no real data.

## Tools (already exist — don't rewrite)

In `python-scripts/`:

| Script | Purpose |
|---|---|
| `decode_session.py <uuid>` | Dump every `SessionLogs.Content` chunk for a session to `<uuid>.decoded.txt` with ANSI sequences spelled out (`\e[2J`, `\e]...\a`, etc.). Header per chunk shows id/timestamp/length. |
| `decode_session.py --list [N]` | List N most recent sessions (default 20). Use when only a prefix is given. |
| `show_chunks.py <uuid> <chunk_ids...>` | Dump specific chunks only. Useful after `analyze_doubleprint` flags a range. |
| `analyze_doubleprint.py <uuid>` | Tag chunks with `ERASE_SCREEN` / `HOME` / `EOL_ERASE_xN` / `ALT_SCREEN_*` / `RESIZE` / `CURSOR_*` / `SYNC_ON`. Fingerprints content (ANSI-stripped, whitespace-collapsed) and flags duplicate redraws within 3s. |

All default to `~/.vibe_rails/state.db`; pass `--db` to override.

## Existing regression tests (the pattern to copy)

`Tests/Services/CleanedInput/Session_<8char>_RegressionTests.cs`. Three working examples — pick the one whose shape matches the bug:

- **`Session_8458cd22_RegressionTests.cs`** — `TuiTextExtractor` picked the wrong line: longest-containing won, fuzzy tail-match overwrote with prior prompt, box-drawing rows out-lengthened the real prompt. Inline fixtures only.
- **`Session_bf428817_RegressionTests.cs`** — `@`-autocomplete + TAB: split rows from premature bracketed-paste flush, dropdown picked, keystroke-echo prepended. Inline fixtures **and** byte-level `Fixtures/session_bf428817_log.bin` (~349KB capture).
- **`Session_11553f24_RegressionTests.cs`** — `@`-autocomplete with no space: needs *windowed* fixtures (`window1` = pre-submission, `window2` = post-submission), each bounded by adjacent `UserInputs` timestamps.

Fixtures live at `Tests/Services/CleanedInput/Fixtures/session_<prefix>_log.bin` (or `_window1_log.bin` / `_window2_log.bin` for split rows). Checked-in fixtures are permanent regression coverage, so they must be minimized and sanitized. The raw session capture you used for investigation stays local and untracked.

The test target is `CleanedUserInputService.CleanText(rawInputText, tuiOutputString)`. Construct with mocked `IRepository` (returns empty `GetSessionLogChunksAsync`), real `new TuiTextExtractor()`, `NullLogger<CleanedUserInputService>.Instance`.

## Workflow

### 1. Resolve the UUID
If only a prefix or "the session I just ran" is given:
```
python python-scripts/decode_session.py --list 20
```
Match by recency / prefix. If still ambiguous, ask.

### 2. Decode the raw stream
```
cd python-scripts
python decode_session.py <full-uuid>
```
Produces `<uuid>.decoded.txt` in cwd. Read it. Look for:
- `\e[2J` storms (full redraws)
- CUP sequences (`\e[<row>;<col>H`) gluing unrelated content onto one sanitized line
- Bracketed-paste markers (`\e[200~` / `\e[201~`)
- Prompt glyphs (`> ` and `›` U+203A — Claude Code's submitted-prompt marker)
- Dropdown lines (`+ filename`)

### 3. Pull UserInputs + CleanedUserInput
```
sqlite3 ~/.vibe_rails/state.db "
  SELECT u.Id, u.Sequence, u.TimestampUTC, u.InputText, c.CleanedText
  FROM UserInputs u
  LEFT JOIN CleanedUserInput c ON c.UserInputId = u.Id
  WHERE u.SessionId='<uuid>'
  ORDER BY u.Sequence;"
```
Compare `InputText` vs `CleanedText` vs what the UI showed. The mismatch is the bug.

### 4. Classify

| Symptom | Subsystem | Test location |
|---|---|---|
| `CleanedText` has noise / is too short / matches a *prior* prompt | `CleanedUserInputService` / `TuiTextExtractor` | `Tests/Services/CleanedInput/` (most common) |
| One submission became two `UserInputs` rows | `InputAccumulator` (bracketed-paste flush race) | `Tests/Services/CleanedInput/` (see bf428817 bug 1) |
| Transcript page (`/sessions/<id>`) wrong | `SessionTranscriptService` / `SessionParseV4` | `Tests/ParserTests/SessionParseV4FixtureTests.cs` pattern |
| xterm.js replay double-prints / loses redraws / glitches on resize | `TerminalEmulator` | `TerminalEmulator.Tests/FixtureReplayTests.cs` pattern; run `analyze_doubleprint.py` first |
| No logs at all / chunks missing | `SessionOutputWriter` or chunk-write path | not a parsing bug |

### 5. Capture and sanitize the fixture
Identify the `SessionLogs` byte range corresponding to the failing `UserInput` (timestamp-bounded by adjacent `UserInputs.TimestampUTC` rows). Concatenate those `Content` blobs locally, then write the minimized/sanitized version to:
```
Tests/Services/CleanedInput/Fixtures/session_<8char>_log.bin
```
For split-row bugs, capture two windows: `_window1_log.bin` (pre-submission) and `_window2_log.bin` (post-submission).

Do not check in the first raw capture if it contains real data. Before staging it:
- Trim it to the smallest byte window that reproduces the bug.
- Redact or replace real prompt text, paths, filenames, command output, tokens, IDs, and user/project names.
- Re-run the test against the sanitized fixture to prove the bug still reproduces.
- If redaction changes the terminal behavior and the only reproducer contains real data, keep the raw fixture out of git and commit an inline synthetic reproducer instead, with a note explaining why no binary fixture is checked in.

### 6. Write the failing test
Copy the closest-matching `Session_*_RegressionTests.cs` as a template. Rename namespace, class, fixture path. Required test cases:
- `CleanText(rawInputText, LoadFixture()) → expectedPrompt` against the sanitized `.bin`, when a safe byte-level fixture can be committed
- Inline-fixture variants for the specific syntactic hazard (CUP fusion, prompt-glyph, dropdown-then-prompt, etc.) so the bug is documented standalone

Class-level docstring: name the session UUID, one line per bug observed. Match the existing tone — it's written for future-you.

### 7. Run, confirm red
```
dotnet test --filter Session_<8char>
```
Verify it fails for the right reason (assertion shows the actual extracted text, which should match what was on the UI).

### 8. Fix the smallest piece
Almost always one of:
- A heuristic in `TuiTextExtractor` (`TryPromptLine`, `TryContainingLine`)
- A rule in `InputEtlFilter`
- A flush gate in `InputAccumulator`

Don't refactor surrounding code.

### 9. Run all `Session_*` tests together
```
dotnet test --filter "FullyQualifiedName~Tests.Services.CleanedInput"
```
Cross-regression is real — Session_8458cd22 contains a test (`CleanText_LeadingWhitespaceRaw_ExtractsFullPromptAfterAutocomplete`) that exists specifically to keep its fix from breaking 11553f24.

### 10. Leave the safe fixture and test in place
They are the contract. Don't delete or rename existing session-named test files or sanitized fixtures. Keep raw session exports, decoded files, copied database files, and any private capture used to author the test out of git.

## Don't

- Don't write a new Python script when `decode_session.py` / `analyze_doubleprint.py` / `show_chunks.py` already cover the case.
- Don't check in `state.db`, decoded session `.txt` files, full-session `.bin` captures, or the real session used to write the unit test.
- Don't ship a binary fixture that still contains real prompts, paths, filenames, output, tokens, IDs, or project/user names. Sanitize it first.
- Don't ship a test that uses synthetic TUI strings only when a sanitized byte-level fixture can reproduce the bug. Synthetic strings miss byte-level pathology, but privacy wins over fixture fidelity.
- Don't skip step 3 (sqlite query). Without it you don't know which row is wrong and you'll test the wrong column.
- Don't refactor while fixing. The bug fix and any cleanup go in separate commits.
