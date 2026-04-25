# UserInOut — user text cleaning and read API

Capturing what the user typed, cleaning it, and serving it to consumers through two intent-named interfaces.

**Plan:** `C:\Users\robst\.claude\plans\atomic-honking-clock.md`

## Which interface do I inject?

| If your code… | Inject | Why |
|---|---|---|
| sends text to an LLM / embedder / or a file the user downloads | `IGetCleanedUserText` | must never leak raw secrets; `""` is the correct signal when cleaning hasn't caught up yet |
| shows a preview / display name / tooltip in the UI | `IGetUserText` | empty string is worse than a dirty one; SQL-level `COALESCE(cleaned, raw)` guarantees non-empty when any input exists |
| needs the raw `UserInputRecord` object (archive, `/sessions/{id}/inputs`) | `IRepository.GetUserInputsForSessionAsync` | genuinely wants the whole row, not the text in isolation |

**Reviewer rule.** If a new PR reads `UserInputs.InputText` or `CleanedUserInput.CleanedText` outside `Services/UserInOut/` (or `CleanedUserInputService`, which is the cleaner itself), flag it. One of the three options above covers every real use case.

## Architecture

Three layers:

1. **Read façades** — consumers call these. Two interfaces with different semantics (see table above).
2. **Write pipeline** — idle observer (Part 2a) + closed-session background job (Part 2b) clean raw inputs and persist to `CleanedUserInput`.
3. **TUI text extraction** — waterfall matcher that finds the user's text in TUI output bytes from `SessionLogs`.

## Schema

```
Session → UserInput ↔ CleanedUserInput  (strict 1:1 on the right side)
```

- `UserInputs.CleanedId` — nullable FK → `CleanedUserInput(Id)`. Partial index `WHERE CleanedId IS NULL`.
- `CleanedUserInput.UserInputId` — UNIQUE NOT NULL FK → `UserInputs(Id)`.
- `CleanedInputMapping` table deleted (was always 1:1 in practice).

## Public surface

```csharp
namespace VibeRails.Services.UserInOut;

// Strict — "" when not cleaned yet.
public interface IGetCleanedUserText
{
    Task<string> GetSessionTextAsync(string sessionId, CancellationToken ct = default);
    Task<string> GetTextForInputIdAsync(long userInputId, CancellationToken ct = default);
}

// Best-effort — cleaned if available, otherwise raw InputText (via SQL COALESCE).
public interface IGetUserText
{
    Task<string> GetTextForInputIdAsync(long userInputId, int? maxChars = null, CancellationToken ct = default);
    Task<string> GetFirstInputTextForSessionAsync(string sessionId, int? maxChars = null, CancellationToken ct = default);
}
```

- Neither interface triggers synchronous cleaning — that's the write pipeline's job.
- `IGetCleanedUserText.GetSessionTextAsync` — INNER JOIN `UserInputs` → `CleanedUserInput`, ordered by `Sequence`, skip empties, join with `\n`.
- `IGetUserText.*` methods — single-row lookup with `COALESCE(c.CleanedText, u.InputText)`. Returns `""` only when the row doesn't exist.

## Write pipeline

- **Part 2a — `CleanedInputIdleObserver : ITerminalIoObserver`** — fires on existing `TerminalIdleEvent` (5s idle). Scans own session for `UserInputs WHERE CleanedId IS NULL`, cleans each, writes `CleanedUserInput` row + updates `UserInputs.CleanedId` atomically.
- **Part 2b — refactored `CleanedUserInputBackfillJob`** — runs every 5 min, picks up uncleaned rows in sessions ended >5 min ago.
- Both call `CleanedUserInputService.CleanAndPersistAsync`.
- Filtered-out inputs (secrets, noise, empty) get a `CleanedUserInput` row with `CleanedText = ""`.

## TUI text extraction (waterfall)

Replaces `ScreenTextMatcher`. Pure function, takes raw user input + TUI output string.

1. **Tier 1 — Stripped substring match:** strip ANSI from both, end-anchor with last 20%/10%/5% of input, start-anchor with `> ` prompt + 3-char prefix match.
2. **Tier 2 — Raw text match:** same logic on unstripped text.
3. **Tier 3 — FuzzySharp:** Levenshtein-based fuzzy matching.
4. **Tier 4 — Return raw input as-is.**

## Files in this directory

- `IGetCleanedUserText.cs` / `GetCleanedUserText.cs` — strict cleaned-only façade
- `IGetUserText.cs` / `GetUserText.cs` — best-effort cleaned-or-raw façade
- `InputEtlFilter.cs` — ETL filtering (secret detection, noise detection, normalization)
- `ScreenTextMatcher.cs` — legacy, replaced by TUI extraction waterfall
