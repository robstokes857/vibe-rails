# repro-fixtures

Binary captures from real PTY sessions, kept here so terminal bugs stay
reproducible after the originating `~/.vibe_rails/state.db` rows are gone.
Cross-referenced from `runbooks/terminal/TERMINAL.md` entries.

Privacy note: checked-in captures must be sanitized to contain no tokens,
credentials, third parties, or content from sessions unrelated to the bug
being demonstrated. New session captures remain local by default. Before
adding one to git, ask whether that exact capture is OK to track; if approved,
add a narrow `.gitignore` exception for the exact file instead of weakening the
global raw-session ignore rules. The user's repo path (`C:\source\vibe-rails`)
and Windows username (`robst`) may appear in ANSI escape sequences that paint a
TUI — those are public-information by being checked into this repo anyway.

## Files

### `session_f3e25a1e_resize_reprint.bin` (3486 B)

The two suspect `SessionLogs` chunks from session
`f3e25a1e-c0eb-4834-a3d2-0eace2bb0e1f` (2026-05-13), concatenated in
arrival order:

| Offset | Length | SessionLogs.Id | Timestamp (UTC)       | Notes                                  |
|-------:|-------:|---------------:|-----------------------|----------------------------------------|
|      0 |   1512 |        5783642 | 2026-05-13 05:29:25.181 | Partial paint — bottom UI at rows 1-9. |
|   1512 |   1974 |        5783643 | 2026-05-13 05:29:25.189 | Full paint — banner rows 1-3, bottom UI rows 7-15. |

Both chunks start with `\e[H` (CUP home) and were emitted by Claude Code
in response to a single SIGWINCH (rows 10 → 28). 7 ms apart. This is the
2026-05-13 resize-reprint bug — see the top of `TERMINAL.md` for the full
forensic write-up and why we have not fixed it yet.

To eyeball the chunks again (run from the repo root — the script lives in
`python-scripts/` at the top level):

```
python python-scripts/show_chunks.py f3e25a1e-c0eb-4834-a3d2-0eace2bb0e1f 5783642 5783643
```

To replay the bytes into a fresh emulator for a regression test:

```csharp
var bytes = File.ReadAllBytes("runbooks/terminal/repro-fixtures/session_f3e25a1e_resize_reprint.bin");
// feed `bytes[0..1512]` and `bytes[1512..]` as two separate writes, geometry 150x28.
```
