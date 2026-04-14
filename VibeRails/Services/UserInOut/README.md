# UserInOut — user text cleaning and read API

Cleaned user input: capturing what the user typed, cleaning it, and serving it to consumers.

**Plan:** `C:\Users\robst\.claude\plans\atomic-honking-clock.md`

## Architecture

Three layers:

1. **Read façade (`IUserTextOutput`)** — consumers call this. Returns `string`. Uncleaned rows get `""`.
2. **Write pipeline** — idle observer (Part 2a) + closed-session background job (Part 2b) clean raw inputs and persist to `CleanedUserInput`.
3. **TUI text extraction** — waterfall matcher that finds the user's text in TUI output bytes from `SessionLogs`.

## Schema

```
Session → UserInput ↔ CleanedUserInput  (strict 1:1 on the right side)
Session → TUI_Event                    (parsed control-key events)
```

- `UserInputs.CleanedId` — nullable FK → `CleanedUserInput(Id)`. Partial index `WHERE CleanedId IS NULL`.
- `CleanedUserInput.UserInputId` — UNIQUE NOT NULL FK → `UserInputs(Id)`.
- `TUI_Event.SessionId` — FK → `Sessions(Id)` with `ON DELETE CASCADE`.
- `CleanedInputMapping` table deleted (was always 1:1 in practice).

## Public surface

```csharp
namespace VibeRails.Services.UserInOut;

public interface IUserTextOutput
{
    Task<string> GetSessionTextAsync(string sessionId, CancellationToken ct = default);
    Task<string> GetTextForInputIdAsync(long userInputId, CancellationToken ct = default);
}
```

- `GetSessionTextAsync` — LEFT JOIN `UserInputs` to `CleanedUserInput`, ordered by `Sequence`, skip empties, join with `\n`.
- `GetTextForInputIdAsync` — single-row lookup. Returns `""` if uncleaned, filtered out, or not found.
- No realtime cleaning. No `HasTextSettled`. No merging. No synchronous fallback.

## Write pipeline

- **Part 2a — `CleanedInputIdleObserver : ITerminalIoObserver`** — fires on existing `TerminalIdleEvent` (5s idle). Scans own session for `UserInputs WHERE CleanedId IS NULL`, cleans each, writes `CleanedUserInput` row + updates `UserInputs.CleanedId` atomically.
- **Part 2b — refactored `CleanedUserInputBackfillJob`** — runs every 5 min, picks up uncleaned rows in sessions ended >5 min ago.
- **TUI event persistence — `TuiEventPersistenceService`** — subscribes to `TUI_Event_Watcher` once at startup and persists `TUI_Event` rows asynchronously (`SessionId`, `TimestampUTC`, `TriggerString`, `EventType`).
- Both call `CleanedUserInputService.CleanAndPersistAsync`.
- Filtered-out inputs (secrets, noise, empty) get a `CleanedUserInput` row with `CleanedText = ""`.

## TUI text extraction (waterfall)

Replaces `ScreenTextMatcher`. Pure function, takes raw user input + TUI output string.

1. **Tier 1 — Stripped substring match:** strip ANSI from both, end-anchor with last 20%/10%/5% of input, start-anchor with `> ` prompt + 3-char prefix match.
2. **Tier 2 — Raw text match:** same logic on unstripped text.
3. **Tier 3 — FuzzySharp:** Levenshtein-based fuzzy matching.
4. **Tier 4 — Return raw input as-is.**

## Files in this directory

- `IUserTextOutput.cs` — read façade interface
- `UserTextOutput.cs` — implementation
- `InputEtlFilter.cs` — ETL filtering (secret detection, noise detection, normalization)
- `ScreenTextMatcher.cs` — legacy, replaced by TUI extraction waterfall
