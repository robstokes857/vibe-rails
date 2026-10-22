# VibeRails Rules (DB layer)

Rules VibeRails enforces on commits under VibeRails.Data.Sqlite/DB. Each rule is a list item under the
rules heading below; the optional Files section lists files covered by this policy.

## Vibe Rails Rules
- Log file changes > 10 lines (WARN)

## Files
- [Repository.Recordings.cs](Repository.Recordings.cs): check byte/row budgets with payload-free SQLite queries before loading recording contents in the same read snapshot; retain all stored data and enforce native-memory bounds.
- [LlmExchangeLogStore.cs](LlmExchangeLogStore.cs): persist requested and served model metadata separately; adopt nullable ServedModel through proxy-exchanges/3 without backfill, a new index or a generation bump so older writers remain compatible.
- [JobStore.BoardRecipes.cs](JobStore.BoardRecipes.cs): install each new-board review Worker, ordered checks, Job and stable recipe receipt in one state transaction; retries preserve user edits and never recreate deleted recipes.
- `JobStore.Board.cs`: retain busy lane demand, distinguish terminal rejection and committed duplicates, and acknowledge dispatch outcomes through IBoardStore after the independent run commit.
- [JobStore.cs](JobStore.cs): snapshot review purpose when queuing/retrying runs, keep Code review retry card context independently of routing, and resolve starter scope defaults through the stable recipe receipt without replacing saved card settings.
- [SqlStrings.cs](SqlStrings.cs): include explicit purpose in Environment reads/writes while keeping Work as the default for older writers; mark new sessions title-eligible and report whether a session's name is its generated conversation title.
- [Repository.cs](Repository.cs): enroll a new session's first input for a conversation title in the same write, and cancel pending title work in the same transaction as an explicit rename.
- [AGENTS.md](AGENTS.md): document the conversation-title tables, claims and backoff.
