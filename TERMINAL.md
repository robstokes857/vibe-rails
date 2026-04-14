# TERMINAL.md

## 2026-04-09 Attach path unified: "be a correct terminal, no per-CLI hacks"

This repo no longer contains any per-CLI attach-path branching. The design
principle going forward:

> **The terminal backend must behave as a correct PTY/ANSI/VT100 implementation.
> If a CLI misbehaves, either our emulator is wrong (and the fix benefits
> everyone) or the CLI is wrong (and it's upstream's job to fix). No
> `if cliName == X then do Y` branches.**

### What changed

1. **Deleted `TerminalReplayPolicy.cs`** — the `codex → redraw-attach` carveout
   is gone. Local and remote attach use one path for every CLI.
2. **New atomic primitive `Terminal.SubscribeWithSnapshot(ITerminalConsumer)`**
   captures the emulator state, delivers it to the consumer as its first
   output, and subscribes it to live PTY bytes — all under
   `_subscriberLock` + `_emulatorLock`. Guarantee: the consumer sees
   `(snapshot at time T)` followed by `(every PTY byte dispatched after T)`
   in order, with no gap and no duplication.
3. **`Terminal.PushSnapshotTo(ITerminalConsumer)`** is the companion for
   already-subscribed consumers (remote PIN verify, remote replay request).
   The snapshot begins with `ED2` + `ED3` + cursor home so it is
   self-healing — whatever the viewer had on screen is wiped and rebuilt.
4. **`ReadLoopAsync` and `PublishOutput` now hold `_subscriberLock` across the
   full dispatch.** Previously they snapshotted the consumer list under lock
   then iterated outside. Holding the lock for the whole dispatch is what
   makes `SubscribeWithSnapshot` atomic: a new subscriber can only join
   *between* dispatches, never mid-dispatch.
5. **Removed every `Ctrl+L`-on-attach poke.** The old "send Ctrl+L to cover
   the gap between snapshot capture and subscription" hack is gone from both
   `TerminalSessionService.HandleWebSocketAsync` and
   `TerminalRunner.HandleRemoteReplayRequestAsync` /
   `HandleRemoteInputAsync` (pin verify). There is no gap anymore, so there
   is nothing to cover.
6. **Removed `replayInProgress` pause flag** from `TerminalRunner` remote
   path. `RemoteOutputConsumer.canForward` now only checks viewer
   authorization, not replay state.
7. **Removed unused `s_activeCli` state** from `TerminalSessionService` —
   attach was its only reader.

### Why this fixes the stacked-banner bug

Claude Code (and Codex/Copilot) react to `Ctrl+L` by emitting a full TUI
repaint that includes the banner. Because those CLIs run on the main screen
(no `DECSET 1049`), each repaint's banner rows eventually scroll into the
emulator's scrollback via normal scroll-up. Over multiple reconnects the
scrollback accumulates one stacked banner per reconnect.

With the Ctrl+L poke removed and the subscribe/snapshot race fixed properly,
there is no more spurious repaint on attach — the viewer sees exactly what
was on screen, nothing more.

### Invariant that must be preserved

All `ITerminalConsumer.OnOutput` implementations must be non-blocking. The
contract is documented on the interface. If a future consumer wants to do
blocking I/O in `OnOutput`, it must queue to a background worker (the way
`WebSocketConsumer` and `RemoteTerminalConnection` already do via their
internal channels). Otherwise `ReadLoopAsync` will stall and PTY output will
back up.

### What we are explicitly not doing

- No workaround for Codex flicker. If flicker remains after this pass, it's
  either a genuine emulator correctness bug (which we'll chase on its own
  merits) or Codex's problem. Do not re-add a per-CLI branch to paper it over.
- No poking TUIs with Ctrl+L, SIGWINCH, or any other redraw hint as a
  synchronization mechanism. Real terminals don't do this.

Validation after this pass:

- `dotnet test .\TerminalEmulator.Tests\TerminalEmulator.Tests.csproj --nologo`
  -> 764 passed
- `dotnet test .\Tests\Tests.csproj -o /tmp/vb-tests --nologo`
  -> 168 passed

---

## 2026-03-18 follow-up audit after backend/emulator hardening

This repo has now landed the terminal backend/emulator hardening pass plus expanded regression
coverage.

Status against the 2026-03-17 review:

- Fixed before this pass: finding 9 (`ESC \` termination inside DCS/APC/PM/SOS passthrough).
- Fixed in this pass: findings 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14, 15, 16, and 17.
- Resolved as documentation/architecture drift: finding 10. The current attach policy is the
  atomic `SubscribeWithSnapshot` primitive (see 2026-04-09 section) — one code path for every
  CLI, with no post-attach redraw poke. The old managed-AI "skip replay" note and the
  "codex → redraw-attach" carveout later in this file are both historical and superseded.
- Reduced but not fully eliminated: finding 18. Stale cleanup is now activity-aware
  (`SessionLogs` / `UserInputs`) instead of age-only, but it still is not a true OS-process
  liveness check.

New regression coverage added in this pass:

- `CSI 3 J` clears scrollback without erasing the visible screen
- multi-parameter private CSI mode handling applies every mode in the sequence
- resize marks newly-added rows dirty
- supplementary-plane glyphs round-trip through snapshot/screen text
- snapshot normalization now collapses surrogate pairs consistently for golden fixtures

Validation after the hardening pass:

- `dotnet test .\TerminalEmulator.Tests\TerminalEmulator.Tests.csproj --no-restore --nologo`
  -> 756 passed
- `dotnet test .\Tests\Tests.csproj --no-restore --nologo`
  -> 139 passed

---

## 2026-03-17 Deep backend C# code review (historical snapshot)
I found several remaining C#-side hazards that can still produce:

- duplicate or orphaned terminal sessions
- output loss or reordered DB logs under heavy throughput
- unbounded memory growth when a browser/relay cannot keep up
- replay fidelity drift after scrollback clears / remote reattach
- parser desynchronization on less-common escape sequences
- CLI session hosts that can linger after the PTY has already exited
- cleanup paths that are not fully transactional


### Backend architecture summary (current)

The current C# flow is:

1. Session startup enters through `TerminalRoutes`, `TerminalTabsRoutes`, or the CLI bootstrap path
   in `CliLoop` / `TerminalRunner`.
2. `TerminalRunner.CreateSessionAsync()` creates the DB session, builds launch command/env, creates
   the PTY-backed `Terminal`, wires emulator/logging consumers, optionally wires the remote relay,
   then sends the launch command.
3. `Terminal.ReadLoopAsync()` is the single PTY output source. It fans each chunk out synchronously
   to all current `ITerminalConsumer`s.
4. `TerminalEmulatorConsumer` builds the in-memory screen state; `DbLoggingConsumer` writes PTY
   output into the session DB; `WebSocketConsumer` and `RemoteOutputConsumer` forward bytes to local
   and remote viewers.
5. Reconnect / local attach goes through
   `TerminalSessionService.HandleWebSocketAsync()`, which pre-resizes the PTY and then calls
   `Terminal.SubscribeWithSnapshot(consumer)` — one atomic operation that captures emulator state,
   delivers it as the consumer's first output, and subscribes the consumer to live PTY bytes, all
   under `_subscriberLock`. No post-attach Ctrl+L poke. (See the 2026-04-09 section above.)
6. Stop / exit / teardown is split across `TerminalSessionService.StopSessionAsync()`,
   `CleanupAsync()`, the PTY `Exited` event, `Terminal.DisposeAsync()`, and
   `TerminalStateService.CompleteSessionAsync()`.

That architecture is reasonable. The remaining problems are mostly around **serialization of
ownership transitions**, **backpressure**, and **state-model fidelity**.

### High-severity findings

#### 1. `StartSessionAsync()` has a real single-session race

`TerminalSessionService.StartSessionAsync()` checks `s_terminal` under `s_lock`, but it does **not**
reserve the slot before awaiting `_runner.CreateSessionAsync()`. That means two concurrent callers
can both observe `s_terminal == null`, both leave the lock, and both create PTYs/sessions. The
route layer does a separate `HasActiveSession` pre-check, but that does not close the race because
it is outside the same critical section.

Files:
- `VibeRails/Services/Terminal/TerminalSessionService.cs:74-120`
- `VibeRails/Routes/TerminalRoutes.cs:20-87`

Why this matters:
- two PTYs can be created even though the design assumes only one
- one session can overwrite static ownership state created by the other
- leaked/orphaned sessions become much more likely when startup fails mid-flight

This is one of the highest-priority hardening items.

#### 2. PTY output logging is fire-and-forget, unordered, and unbounded

The PTY read loop is synchronous, but DB logging is not. `DbLoggingConsumer.OnOutput()` calls
`TerminalStateService.LogOutput()`, and that method immediately launches an unawaited async insert:

`_ = _dbService.LogSessionOutputAsync(sessionId, data.ToArray(), false);`

Files:
- `VibeRails/Services/Terminal/Consumers/DbLoggingConsumer.cs:17-20`
- `VibeRails/Services/Terminal/TerminalStateService.cs:81-97`
- `VibeRails/Services/DbService.cs:169-186`

Why this matters:
- every PTY chunk creates a new asynchronous SQLite write task
- heavy TUI output can create a very large number of concurrent pending writes
- SQLite will serialize the actual writes, but task scheduling can still drift from PTY emission
  order, so `SessionLogs.Id` is not guaranteed to remain a perfect "PTY order" proxy
- shutdown can complete the session before all pending output writes have drained

This is a major stability risk because it affects **memory**, **ordering**, and **durability** all
at once. The backend needs a **bounded single-writer output pipeline** here, not raw fire-and-forget
tasks.

#### 3. Local and remote output queues are both unbounded

`WebSocketConsumer` copies every PTY chunk into an unbounded `Channel<byte[]>`. The remote relay
path does the same thing in `RemoteTerminalConnection` with an unbounded outbound channel.

Files:
- `VibeRails/Services/Terminal/Consumers/WebSocketConsumer.cs:12-31`
- `VibeRails/Services/Terminal/Consumers/WebSocketConsumer.cs:39-57`
- `VibeRails/Services/Terminal/RemoteTerminalConnection.cs:15-18`
- `VibeRails/Services/Terminal/RemoteTerminalConnection.cs:68-72`
- `VibeRails/Services/Terminal/RemoteTerminalConnection.cs:180-209`

Why this matters:
- a slow browser, hidden tab, broken socket, or slow relay can fall behind indefinitely
- the producer side never slows down and never drops
- each frame is copied first, so backlog growth is pure memory growth

Under a noisy TUI, this can turn into a process-wide memory problem surprisingly fast. A bounded
queue with an explicit policy is needed: backpressure, disconnect-on-overflow, or drop-old frames.
Right now the policy is effectively "allocate until the process hurts."

#### 4. Startup failure is not transactional and can leak PTYs / sessions

`TerminalRunner.CreateSessionAsync()` creates the DB session first, then creates the PTY, then wires
consumers/remote state, then sends the launch command. If anything throws **after** the PTY exists
but **before** the method returns, the caller has no reference to the created terminal yet.

`TerminalSessionService.StartSessionAsync()` catches and calls `CleanupAsync()`, but `s_terminal`
has not been assigned at that point, so there is nothing for `CleanupAsync()` to dispose.

Files:
- `VibeRails/Services/Terminal/TerminalRunner.cs:31-59`
- `VibeRails/Services/Terminal/TerminalRunner.cs:324-327`
- `VibeRails/Services/Terminal/TerminalSessionService.cs:81-127`

Why this matters:
- PTY process can leak on launch failure
- DB session row can remain open even though startup failed
- remote registration may be partially completed

This should be converted into a local transactional pattern inside `CreateSessionAsync()`: create,
wire, and on any failure dispose terminal + complete session before rethrowing.

#### 5. PTY exit handling is wired through an `async void` event handler

`terminal.Exited += async (sender, exitCode) => { ... }` is attached inside
`TerminalSessionService.StartSessionAsync()`. Because `Exited` is an `EventHandler<int>`, this
lambda is effectively `async void`.

Files:
- `VibeRails/Services/Terminal/TerminalSessionService.cs:100-106`
- `VibeRails/Services/Terminal/Terminal.cs:35-37`
- `VibeRails/Services/Terminal/Terminal.cs:268-278`

Why this matters:
- exceptions after the first `await` do not flow in a controllable way
- completion/cleanup ordering becomes much harder to reason about
- it is easy to accidentally create double-complete / double-cleanup paths that "usually work"
  until one throws at the wrong time

Even if most of the current paths are idempotent enough to survive double calls, the pattern itself
is fragile. Exit completion should be funneled through an explicit task-returning method with a
single idempotent gate.

#### 6. `ED3` (`CSI 3 J`) is modeled incorrectly in the emulator

This is one of the most important replay-fidelity bugs still in the backend.

`TerminalBuffer.EraseInDisplay(3)` explicitly treats mode 3 the same as mode 2:

> `case 3: // whole screen + scrollback (treat same as 2 for now)`

That is wrong in two ways:

1. it does **not** clear the emulator's scrollback ring
2. it **does** clear the visible screen by falling through to whole-screen erase semantics

The second point matters because the serializer itself uses `2J` **and then** `3J`, which strongly
implies the intended model is:

- `2J` clears the visible screen
- `3J` clears scrollback

Files:
- `TerminalEmulator/TerminalBuffer.cs:164-183`
- `TerminalEmulator/TerminalBuffer.cs:342-350`
- `VibeRails/Services/Terminal/TerminalGridSerializer.cs:34-37`

Why this matters:
- shells / TUIs can intentionally clear saved history with `CSI 3 J`
- the live terminal drops that scrollback
- the emulator keeps it
- reconnect replay can therefore resurrect history the app intentionally cleared
- if `3J` is sent without `2J`, the emulator also clears the visible screen when it should not

This is not cosmetic. It is exactly the kind of "I know I cleared that, why did it come back after
reconnect?" problem that makes terminal state feel haunted.

Hardening direction:
- add an explicit **clear scrollback** operation to `TerminalBuffer`
- implement true `ED3` semantics instead of aliasing to `ED2`
- add replay tests proving that after `CSI 3 J`, old scrollback does not come back on reconnect

#### 7. Remote replay currently depends on hidden-cursor replay without a guaranteed redraw, and resize is not serialized with replay

`TerminalGridSerializer.Serialize()` always starts replay with `?25l` and intentionally does **not**
restore cursor visibility. The local WebSocket attach path compensates by sending `Ctrl+L` after the
snapshot, which lets the app redraw and re-establish cursor state. The remote replay paths do **not**
do that.

There is a second problem here: remote resize is not funneled through the same replay gate.
`OnResizeRequested` applies PTY resize immediately, while replay capture/sending runs behind
`takeoverGate`. That means remote replay and remote resize are not fully serialized as one state
transition.

Files:
- `VibeRails/Services/Terminal/TerminalGridSerializer.cs:31-69`
- `VibeRails/Services/Terminal/TerminalSessionService.cs:213-221`
- `VibeRails/Services/Terminal/TerminalRunner.cs:149-155`
- `VibeRails/Services/Terminal/TerminalRunner.cs:220-233`
- `VibeRails/Services/Terminal/TerminalRunner.cs:277-295`

Why this matters:
- local replay has an explicit redraw follow-up
- remote replay currently does not
- remote correctness is therefore dependent on later terminal output or later user input
- a resize can race with remote replay and produce a snapshot at the wrong geometry boundary

That is not a reliable invariant. If replay requires redraw for correctness, **every** replay path
must enforce it, or the serializer needs to become self-sufficient again in a safer way.

#### 8. Native CLI session lifetime can hang because the console input loop is not tied to PTY exit and `Console.ReadKey()` is not cancellable

The CLI paths (`RunCliAsync()` and `RunCliWithWebAsync()`) both block on `ConsoleInputLoopAsync()`.
That loop only checks `ct.IsCancellationRequested`, but the actual wait is `Console.ReadKey()`,
which is synchronous and not cancellable. It is also not connected to terminal exit.

Files:
- `VibeRails/Services/Terminal/TerminalRunner.cs:388-418`
- `VibeRails/Services/Terminal/TerminalRunner.cs:425-489`
- `VibeRails/Services/Terminal/TerminalRunner.cs:495-511`

Why this matters:
- if the PTY process exits naturally, the CLI host can keep waiting for keyboard input
- if cancellation is requested, `Console.ReadKey()` may still sit there until another keypress
- external-terminal unregister/cleanup in `RunCliWithWebAsync()` is delayed until that loop finally
  unwinds

This can create very confusing behavior where the terminal process looks finished, but the host
process, web exposure, or cleanup path lingers.

#### 9. The parser can desynchronize on DCS/APC/PM/SOS strings terminated with `ESC \`

The parser enters `DcsPassthrough` for `ESC P` (DCS) and also for SOS / PM / APC. In that state it
only exits on BEL or `0x9C`. It does **not** handle the common `ESC \` string terminator while in
`DcsPassthrough`.

Files:
- `TerminalEmulator/AnsiParser.cs:113-116`
- `TerminalEmulator/AnsiParser.cs:139-145`
- `TerminalEmulator/AnsiParser.cs:168-171`

Why this matters:
- if a DCS-like string is terminated with `ESC \`, the parser can stay in passthrough mode
- once that happens, subsequent PTY output can be swallowed or ignored until another recognized
  terminator appears
- this is a classic state-machine desync bug: rare, catastrophic, and hard to reproduce

This is the kind of parser bug that can make a session suddenly look blank, frozen, or partially
missing after one unexpected sequence.

#### 10. The code no longer enforces the documented "managed AI CLI skip replay" rule

This file currently says managed AI CLIs (`Claude`, `Codex`, `Gemini`, `Copilot`) should skip local
replay and use redraw-first attach. The current C# implementation does **not** do that. It reads
`s_activeCli`, but does not branch on it. It always sends `GetGridReplay()`, then subscribes the
consumer, then sends `Ctrl+L`.

Files:
- `VibeRails/Services/Terminal/TerminalSessionService.cs:130-221`
- `TERMINAL.md:84-107` (existing tracker note describing the old guardrail)

Why this matters:
- a documented regression guardrail is no longer encoded in the backend
- if unified replay is now considered safe, the tracker/doc needs to say so explicitly
- if it is **not** safe, the code is currently missing an important safety branch

For a bug class that has already regressed before, documentation/code drift here is dangerous.

### Medium-severity findings

#### 11. Stop/cleanup does not explicitly close the active local WebSocket

`StopSessionAsync()` completes the session and then calls `CleanupAsync()`. `CleanupAsync()` clears
terminal/session state and disposes the terminal, but it does **not** close `s_activeWebSocket`.
That socket is only cleared in the WebSocket handler finally block or via explicit disconnect/takeover.

Files:
- `VibeRails/Services/Terminal/TerminalSessionService.cs:299-315`
- `VibeRails/Services/Terminal/TerminalSessionService.cs:317-339`
- `VibeRails/Services/Terminal/TerminalSessionService.cs:581-610`
- `VibeRails/Services/Terminal/TerminalSessionService.cs:387-473`

Why this matters:
- browser can remain connected while the terminal has already been torn down
- input loop can continue running against a disposed terminal instance
- close semantics become timing-dependent instead of explicit

The stop path should explicitly disconnect the active local viewer before or during cleanup.

#### 12. `Terminal.DisposeAsync()` waits for read-loop exit before killing the PTY

Current ordering is:

1. cancel `_cts`
2. await `_readLoop`
3. `_pty.Kill()`

Files:
- `VibeRails/Services/Terminal/Terminal.cs:211-227`

Why this matters:
- if the PTY read does not unblock promptly on cancellation, dispose can hang
- shutdown behavior is then dependent on stream cancellation semantics of the PTY layer

The safer pattern is usually to close/kill the producer first (or race wait-vs-kill with a timeout),
then await the read loop.

#### 13. Initial terminal title injection still writes OSC to PTY stdin instead of the output path

`Terminal.CreateAsync()` still writes the title-setting OSC sequence directly to `pty.WriterStream`.
That is PTY **stdin**, not terminal output. Elsewhere in the backend, the code already recognizes
that title OSC must go through the output path via `Terminal.PublishOutput()` rather than stdin.

Files:
- `VibeRails/Services/Terminal/Terminal.cs:74-80`
- `VibeRails/Services/Terminal/Terminal.cs:149-155`
- `VibeRails/Services/Terminal/TerminalRunner.cs:84-89`

Why this matters:
- on ConPTY / terminal-emulator stacks, OSC on stdin is not a reliable title mechanism
- best case, the title does not change
- worse case, the shell or TUI receives raw control bytes as input

This is not necessarily the highest-frequency bug, but it is an unnecessary source of startup
weirdness and it directly contradicts the newer output-path guidance already present elsewhere in
the code.

#### 14. Non-BMP / supplementary wide-character handling is lossy

The emulator stores only one `char` per cell. For code points above `U+FFFF`, `AnsiParser` writes
only the high surrogate and marks the cell wide; it does **not** store the actual rune as a complete
logical character.

Files:
- `TerminalEmulator/AnsiParser.cs:547-566`
- `TerminalEmulator/TerminalBuffer.cs:70-105`
- `VibeRails/Services/Terminal/TerminalGridSerializer.cs:79-97`
- `VibeRails/Services/Terminal/TerminalGridSerializer.cs:109-127`

Why this matters:
- emoji / supplementary-plane glyphs cannot round-trip correctly
- serializer can emit invalid or replacement output
- cell-width and glyph-content accuracy diverge

This will not break every CLI, but it is absolutely the kind of thing that creates "random visual
weirdness" once a tool prints emoji, fancy status icons, or non-BMP glyphs.

#### 15. `TerminalBuffer.Resize()` does not resize `_dirtyRows`

The emulator reallocates `_normal` and `_alternate` on resize, but `_dirtyRows` is `readonly` and
never resized.

Files:
- `TerminalEmulator/TerminalBuffer.cs:40-41`
- `TerminalEmulator/TerminalBuffer.cs:303-331`
- `TerminalEmulator/TerminalBuffer.cs:362-374`

Why this matters:
- rows added during growth can never be marked dirty
- `OnRender` becomes incorrect after certain resizes

This is not on the hottest VibeRails runtime path today because replay uses full snapshots, not
dirty-row incremental rendering. Still, it is a correctness bug inside the emulator core.

#### 16. Private CSI mode handling only applies the first mode parameter

`DispatchCsiPrivate()` is called with only `p0`, even if the escape sequence contained multiple
private mode params.

Files:
- `TerminalEmulator/AnsiParser.cs:275-335`
- `TerminalEmulator/AnsiParser.cs:337-364`

Why this matters:
- multi-mode private sequences are only partially modeled
- most mouse/focus modes are intentionally ignored, but the limitation is still real
- future terminal behavior can regress silently if an important mode is not first

#### 17. Remote registration/deregistration is awaited on the critical path despite the comment

`RemoteStateService` claims these are "fire-and-forget operations that don't block terminal
startup/shutdown." They are not. `RegisterTerminalAsync()` and `DeregisterTerminalAsync()` both
await `HttpClient.SendAsync()`, and `TerminalStateService` awaits those methods during create and
complete.

Files:
- `VibeRails/Services/Terminal/RemoteStateService.cs:11-18`
- `VibeRails/Services/Terminal/RemoteStateService.cs:50-79`
- `VibeRails/Services/Terminal/RemoteStateService.cs:86-108`
- `VibeRails/Services/Terminal/TerminalStateService.cs:67-70`
- `VibeRails/Services/Terminal/TerminalStateService.cs:215-219`

Why this matters:
- a slow or unhealthy remote service can delay local startup/teardown
- the comment currently overstates the safety of this path

#### 18. `StaleSessionCleanupJob` can mark live sessions as stale based on age alone

The job looks for any DB session with `EndedUTC IS NULL` and `StartedUTC < now - 5 minutes` and
marks it complete with exit code `-1`. It does not check whether the session is still running or
still receiving activity.

Files:
- `VibeRails/Jobs/StaleSessionCleanupJob.cs:27-39`
- `VibeRails/Services/DbService.cs:188-203`

Why this matters:
- long-running live sessions can be marked "ended" in history while they are still active
- cleanup semantics are based on age, not liveness

This may not kill the PTY, but it absolutely weakens the reliability of session-state diagnostics.

### Lower-severity / diagnostic findings

- `Terminal.PublishOutput()` swallows all consumer exceptions silently, which makes output-path
  failures harder to debug. `VibeRails/Services/Terminal/Terminal.cs:155-165`
- `InputAccumulator` also uses an unbounded channel. It is lower risk than PTY output because it
  operates on completed lines, but it still has no hard cap. `VibeRails/Utils/InputAccumulator.cs:20-22`
- `TerminalIoObserverService` fan-out is fire-and-forget and unbounded. Fine for lightweight
  observers, but risky if observers become expensive. `VibeRails/Services/Terminal/TerminalIoObserverService.cs:48-100`
- `CommandService` ignores `initialPrompt` entirely right now, which is not a stability bug but is
  surprising API behavior. `VibeRails/Services/Terminal/CommandService.cs:34-87`

### What is already solid

These parts are materially better than the old design and should be preserved:

- **One PTY read loop per terminal** with synchronous fan-out from a single source:
  `VibeRails/Services/Terminal/Terminal.cs:229-279`
- **Snapshot-before-subscribe ordering** in local attach, which avoids replay/live interleave:
  `VibeRails/Services/Terminal/TerminalSessionService.cs:186-209`
- **Pre-resize before replay**, which is the right fix for stale-geometry double-draw:
  `VibeRails/Services/Terminal/TerminalSessionService.cs:164-183`
- **Emulator-backed replay** instead of breakpoint-based raw byte replay:
  `VibeRails/Services/Terminal/Terminal.cs:173-188`
- **Session lifecycle owner tracking** for watchdog survival:
  `VibeRails/Services/LocalClientLifecycle.cs:94-139`,
  `VibeRails/Services/Terminal/TerminalSessionService.cs:118,211,240,258,281,599`
- **Parent PID watchdog** in tab children to prevent orphan child servers:
  `VibeRails/Program.cs:282-356`

### Hardening order I would recommend

If the goal is "make this backend as bulletproof as possible," I would harden in this order:

1. **Serialize session startup/shutdown with a real process-global gate**
   - Fix the `StartSessionAsync()` race first.

2. **Replace fire-and-forget DB output logging with a bounded single-writer pipeline**
   - This is the biggest operational stability gap in the current code.

3. **Bound local/remote output queues**
   - Pick an explicit overflow policy instead of allowing unbounded growth.

4. **Make startup and shutdown transactional**
   - No leaked PTYs, no partially-open DB sessions, no open sockets after stop.

5. **Fix emulator fidelity around `ED3`, cursor state, remote replay, and parser desync**
   - This is the path most likely to create "hard to reproduce" visual regressions.

6. **Fix native CLI lifetime handling**
   - `Console.ReadKey()` must not be the thing keeping dead PTY sessions alive.

7. **Remove `async void` session-exit completion**
   - Move to an idempotent, task-based completion path.

8. **Decide the attach policy for managed AI CLIs and encode it in code**
   - Either replay-first is now safe and should be documented/tested, or the old skip-replay
     policy should be restored.

9. **Improve wide/non-BMP handling if fidelity matters**
   - Especially if modern CLIs with emoji/status glyphs are expected.

### Testing note

The old POC still has useful emulator tests in:

- `C:\Users\robst\Desktop\headless\TerminalEmulator.Tests\AnsiParserTests.cs`
- `C:\Users\robst\Desktop\headless\TerminalEmulator.Tests\FixtureReplayTests.cs`

Those are worth porting into this repo, but the highest-value missing tests for the current backend
are actually **serializer/replay pipeline tests**:

- `TerminalGridSerializer` replay should preserve cleared scrollback semantics
- local replay and remote replay should have explicit cursor-state expectations
- chunked PTY output vs full replay should produce identical snapshots
- stop/exit/start races should be covered with session-lifecycle tests

Terminal problem tracker for the Web UI terminal stack.

Date started: 2026-03-07

## Active Issues

Post-hardening remaining work is smaller and lower-risk:

- stale-session cleanup is activity-aware now, but still not true process-liveness detection
- `InputAccumulator` still uses an unbounded channel (lower risk than PTY output, but not bounded)
- `TerminalIoObserverService` still fans out on fire-and-forget tasks with no hard cap
- end-to-end lifecycle/replay race coverage is still thinner than the emulator/parser regression suite

---

## Deferred / Parked

### Native CLI remote alerting deferred — remote disabled for native sessions

The title-bar notification approach for alerting the local user when a remote viewer connects
proved unreliable (OSC title gets overwritten by the TUI/shell immediately). A proper alerting
layer is planned (interactive system sitting in front of all sessions). Until then, remote
access is disabled for native CLI sessions via `_nativeRemoteEnabled = false` in
`TerminalRunner.ShouldEnableRemote`. Web terminal remote access is unaffected.

**To re-enable:** flip `_nativeRemoteEnabled = true` in `TerminalRunner.cs`.

Key file: `VibeRails/Services/Terminal/TerminalRunner.cs` — `_nativeRemoteEnabled`, `ShouldEnableRemote`

---

## Fixed Issues

### ✅ Backend lifecycle / transport hardening pass (2026-03-18)

This pass closed the core backend hazards from the 2026-03-17 review:

- `TerminalSessionService` startup/stop/detach now runs behind a single lifecycle gate, closing the
  single-session race and making teardown ownership transitions explicit
- PTY output persistence now uses a bounded per-session single-writer pipeline instead of spawning
  unbounded fire-and-forget SQLite writes
- local WebSocket and remote relay output paths are now bounded; overflow explicitly disconnects the
  lagging consumer instead of growing memory without limit
- `TerminalRunner.CreateSessionAsync()` is now transactional: startup rollback disposes the PTY,
  tears down remote connection state, and completes the DB session on failure
- PTY exit cleanup no longer runs as `async void`; exit is funneled through scheduled task-based
  teardown
- stop/exit cleanup now explicitly closes the active local WebSocket, disposes the PTY before
  waiting for the read loop, and flushes queued DB output before completing the session
- title OSC is published on the output path instead of being injected into PTY stdin
- remote register/deregister no longer block the main session critical path
- stale-session cleanup no longer closes sessions on age alone; it now checks recent log/input
  activity first

Key files:
- `VibeRails/Services/Terminal/TerminalSessionService.cs`
- `VibeRails/Services/Terminal/TerminalRunner.cs`
- `VibeRails/Services/Terminal/TerminalStateService.cs`
- `VibeRails/Services/Terminal/Terminal.cs`
- `VibeRails/Services/Terminal/Consumers/WebSocketConsumer.cs`
- `VibeRails/Services/Terminal/RemoteTerminalConnection.cs`
- `VibeRails/Services/DbService.cs`

---

### ✅ Emulator/parser fidelity hardening pass (2026-03-18)

This pass also closed the major emulator-side fidelity gaps from the review:

- `ED3` / `CSI 3 J` now clears scrollback only and no longer erases the visible screen
- remote replay now gets the same redraw follow-up as local replay, and remote resize is serialized
  through the same replay/takeover gate
- native CLI console input loop now exits when the PTY exits instead of lingering on
  `Console.ReadKey()` semantics
- supplementary-plane glyphs now preserve the full Unicode scalar for replay/serialization
- resize now resizes `_dirtyRows` and marks new rows dirty
- private CSI mode handling now applies every mode parameter, not just the first
- snapshot normalization was updated so the expanded non-BMP behavior compares cleanly against
  historical golden files

Finding 9 (`ESC \` terminator handling inside DCS/APC/PM/SOS passthrough) was already fixed before
this pass and remained green.

Key files:
- `TerminalEmulator/AnsiParser.cs`
- `TerminalEmulator/TerminalBuffer.cs`
- `TerminalEmulator/TerminalCell.cs`
- `TerminalEmulator/Terminal.cs`
- `VibeRails/Services/Terminal/TerminalGridSerializer.cs`
- `TerminalEmulator.Tests/AnsiParserTests.cs`
- `TerminalEmulator.Tests/EscapeSequenceTests.cs`
- `TerminalEmulator.Tests/SnapshotTests.cs`

---

### ✅ Attach policy reconciled — unified replay is now the intended baseline (2026-03-18)

This file previously contained two conflicting stories:

- one section said managed AI CLIs should skip replay and use redraw-first attach
- later sections described emulator replay as the reconnect baseline

Current code has standardized on a single attach policy for local and remote viewers:

- resize first (if dimensions are known)
- capture emulator replay
- send replay before subscribing live output
- subscribe live output
- send `Ctrl+L` redraw immediately after replay

That means the old managed-AI skip-replay guidance is no longer the current contract. Keep the older
incident notes below as history, not as current architecture guidance.

---

### ✅ JS cursor settings removed — prevents future cursor state fights (2026-03-17)

**Background:** The ghost cursor fix (below) was a C# change — removing `?25h` from
`TerminalGridSerializer.Serialize()`. That fixed the symptom. This is follow-up cleanup to
ensure no JS can re-introduce the same fight.

**What was removed:**
- `cursorBlink`, `cursorStyle`, `cursorInactiveStyle` options from the xterm.js `Terminal()`
  constructor in `vibe-terminal.js` and `term/term.js`
- `setInteractive()` method in `vibe-terminal.js` — was dead code that toggled `cursorBlink`
  on focus change
- `_loadCursorBlink()`, `_loadCursorStyle()` localStorage loaders in `terminal-multitab.js`
- Cursor lines (`cursorStyle`, `cursorBlink`) from `_applySavedTerminalSettings()`
- `applyCursorStyle()` and `applyCursorBlink()` methods
- Event listeners for cursor style/blink selects
- Entire **Cursor** section (Style + Blink dropdowns) from the Terminal Settings panel HTML

**Why:** TUI apps manage cursor visibility and appearance entirely via escape sequences
(`?25l`/`?25h`, `?12h`, SGR). Any JS that sets `terminal.options.cursorBlink/cursorStyle`
overrides what the TUI intended and can reintroduce ghost cursors.

Key files:
- `VibeRails/wwwroot/js/modules/vibe-terminal.js` — constructor, removed `setInteractive()`
- `VibeRails/wwwroot/js/modules/terminal-multitab.js` — loader/apply methods, settings HTML
- `VibeRails/wwwroot/term/term.js` — standalone remote viewer init

---

### ✅ Ghost / roaming cursor after reconnect — TUI fake cursor vs real xterm.js cursor (2026-03-17)

After reconnect, a second cursor-like block appeared alongside or flew around the viewport
independently of the real cursor. It was most visible during TUI loading and immediately after
replay.

**Root cause (primary — cursor visibility fight):**
TUI apps (Claude Code, etc.) intentionally hide the real xterm.js cursor (`?25l`) and draw their
own block/beam glyph at the prompt. `TerminalGridSerializer.Serialize()` was appending `?25h`
(cursor visible) at the end of every replay. This un-hid the real cursor, giving xterm.js two
"cursors": the real hardware cursor (restored by `?25h`) and the TUI's own drawn block — both
visible simultaneously. Removing `?25h` from the end of replay fixes this entirely. The cursor
stays hidden after replay; the subsequent Ctrl+L redraw causes the TUI to re-establish its own
cursor state naturally.

**Root cause (secondary — cursor flying during repaint):**
The replay sequence began with `ESC c` (RIS hard reset) and painted all screen rows using
sequential CRLF flow (`\r\n` between rows). During repaint, xterm.js rendered the cursor
wherever it currently thought it was, then moved it again at the final CUP — visually the
cursor appeared to fly around. Fix: hide cursor at the very start of replay with `?25l`, and
paint each screen row using absolute CUP addressing (`\x1b[{r+1};1H`) instead of CRLF flow.
This makes it impossible for cumulative drift (wide chars, full-width columns, wrap semantics)
to misplace the cursor during repaint.

**Root cause (tertiary — hard reset side-effects):**
`\u001bc` (RIS) resets many terminal modes beyond screen content. In xterm.js this can cause
cursor state changes, mode resets, and visual artifacts when content is immediately repainted.
Replaced with a targeted soft clear: `?25l` + `ED2` + `ED3` + `CUP(1,1)` — clears screen and
scrollback only, leaves all other terminal modes intact.

**Fix summary (`TerminalGridSerializer.Serialize()`):**
1. Start with `?25l` (hide cursor) + `\x1b[2J\x1b[3J\x1b[H` instead of `ESC c`
2. Scrollback rows unchanged (CRLF flow into xterm scrollback is correct)
3. Each screen row prefixed with `\x1b[{r+1};1H` (absolute CUP, no CRLF)
4. End with `\x1b[0m` + CUP to real cursor position — no `?25h`, no `?12h`

**Fix summary (`TerminalSessionService.HandleWebSocketAsync()`):**
Added a comment documenting the critical ordering invariant: snapshot must be sent before
subscribing the live WebSocket consumer. If the consumer is subscribed first, live PTY output
can arrive at the browser while the snapshot is in-flight, producing a concurrent-write race
that creates ghost cursors and corrupted screen state.

**Also closed:** the "Remaining roaming cursor" active bug (2026-03-16 CSS fix + retest pending)
is confirmed resolved by this change. The CSS `outline: none`/`opacity: 0` fix on
`.vb-terminal-element .xterm-helper-textarea` remains in place as defense-in-depth against the
focus-ring artefact.

Key files:
- `VibeRails/Services/Terminal/TerminalGridSerializer.cs` — `Serialize()`
- `VibeRails/Services/Terminal/TerminalSessionService.cs` — `HandleWebSocketAsync()` comment

---

### ✅ Double print / full-session duplicate replay on reconnect and hard refresh

On reconnect and especially on hard refresh, the browser could repaint the top/full screen
twice or replay the entire visible AI CLI session again. This was most noticeable with
full-screen TUI CLIs like Codex/Claude/Gemini/Copilot.

**Root cause:** the local WebSocket attach path unconditionally sent `terminal.GetGridReplay()`
before subscribing the live WebSocket consumer. For managed AI CLI sessions, this conflicted
with redraw-style attach behavior and caused duplicated TUI content on browser reconnect / hard
refresh. Plain shell / line-oriented sessions could still use replay, but managed AI CLIs
needed redraw-first attach instead.

**Fix:** updated `VibeRails/Services/Terminal/TerminalSessionService.cs` so managed AI CLIs
(`Claude`, `Codex`, `Gemini`, `Copilot`) now skip local replay on attach and instead subscribe
the WebSocket first, then request a redraw with `Ctrl+L`. Plain shell / line-oriented sessions
still use `GetGridReplay()`.

**2026-03-16 retest:** user confirmed this tested good. The duplicate replay / double print
issue no longer reproduces in current testing.

**Superseded 2026-03-18:** local attach no longer uses a managed-AI special case. The backend now
standardizes on emulator replay for all local sessions, with pre-resize, replay-before-subscribe
ordering, and an immediate post-replay `Ctrl+L` redraw. Treat the old skip-replay branch described
above as historical incident context only.

Key files:
- `VibeRails/Services/Terminal/TerminalSessionService.cs` — `HandleWebSocketAsync`, `s_activeCli`
- `VibeRails/wwwroot/js/modules/terminal-multitab.js` — `connect()`, restore/attach flow

---

### ✅ Double/phantom cursor — ghost cursor alongside real cursor

While typing, a second blinking cursor appeared alongside the real cursor. When typing reached
end of a row, the phantom moved to the bottom-right corner of the viewport. Observed in both
browser and VS Code extension.

**Root cause:** xterm.js v6 positions the `xterm-helper-textarea` ON-SCREEN at the cursor
location (for IME composition support), unlike older xterm.js which parked it at `left: -9999em`.
The browser renders the textarea's native caret at that pixel position, producing a second
blinking cursor on top of xterm.js's own canvas-rendered cursor. At end-of-row / pending-wrap,
xterm.js moves the textarea to the wrap position a frame late, leaving the native caret briefly
at the old column — visually "stuck at bottom-right".

**Fix:** `caret-color: transparent !important` on `.terminal-element .xterm-helper-textarea`
in `style.css`. This hides the browser caret while xterm.js's own cursor remains fully visible.

Key file: `VibeRails/wwwroot/style.css` — `.terminal-element .xterm-helper-textarea`

---

### ✅ Cursor flickering during TUI loading

The cursor visibly flickered or jumped around while a TUI app (e.g. Claude Code) was loading.
Observed in both browser and VS Code extension.

**Root cause (primary):** Same as the double/phantom cursor above — xterm.js v6 moves the
textarea to the cursor position on every cursor-movement sequence. TUI apps emit rapid cursor
moves during startup (`\u001b[R;CH`, `\u001b[?25l/h`, etc.), causing the browser native caret
to flicker across the screen as the textarea tracks each move. Fixed by `caret-color: transparent`.

**Root cause (secondary):** `socket.onopen` called `fitAndSyncTerminal()` which force-sent a
`__resize__` control frame even when dimensions were identical to the pre-connect fit already
forwarded in the WebSocket URL. The server received the same-size resize, sent SIGWINCH to the
PTY, and the TUI performed a full redraw right on top of the just-loaded replay — causing an
additional wave of cursor movement and redraw flicker immediately after reconnect.

**Fix:** Prime `this.lastResizeSignature` in `connect()` with the pre-connect dimensions after
the pre-connect fit. Replace `fitAndSyncTerminal()` in `socket.onopen` with a non-forced
`sendResizeToPty()` that skips if the signature is unchanged. The server already has the correct
PTY dimensions from the URL; the post-connect sync only sends `__resize__` if the container
genuinely changed between pre-connect and `onopen`.

Key files:
- `VibeRails/wwwroot/style.css` — `caret-color: transparent`
- `VibeRails/wwwroot/js/modules/terminal-multitab.js` — `connect()` signature priming, `socket.onopen` resize path

---


### ✅ Cursor flicker / jumping cursor positions after reconnect and resize

After the double-print fix, the Web UI terminal could still show the cursor jumping between
old and new positions during reconnect or layout settle. The screenshot looked like "ghost"
cursors being painted in multiple places even though the text itself was no longer duplicated.

**Root cause:** the client-side resize path called `resetDisplayOnly()` on any new geometry,
including the first post-connect sync. That local xterm reset was useful for stale right-edge
cells on real shrink events, but it was too aggressive for reconnect. It briefly cleared and
repainted the local viewport before the PTY had anything new to say, which made the cursor look
like it was flickering or teleporting.

**Fix:** only clear the local xterm viewport when the terminal actually shrinks
(`newCols < oldCols || newRows < oldRows`). The first post-connect sync and grow-only layout
passes now skip the local reset and just send the resize to the PTY.

Key file: `VibeRails/wwwroot/js/modules/terminal-multitab.js` — `sendResizeToPty`,
`shouldResetDisplayBeforeResize`

### ✅ Double paste when pasting into the terminal

Pasting into the web terminal (Ctrl+V or right-click paste) sent the clipboard text twice.

`attachClipboardPaste` in `vibe-terminal.js` had two simultaneous paste paths: (1) a Ctrl+V
keydown handler calling `navigator.clipboard.readText()`, and (2) xterm's own bubble-phase
`paste` listener, which also fired `onData` because the capture listener only called
`e.preventDefault()` — not `e.stopImmediatePropagation()`. xterm does not check
`e.defaultPrevented`, so it always ran regardless.

Fixed by consolidating to a single paste path: the capture-phase `paste` listener now calls
`e.stopImmediatePropagation()` to block xterm's listener. All paste text is read from
`e.clipboardData`. The keydown handler now only returns `false` to prevent xterm from processing
Ctrl+V as a raw key sequence.

Key file: `VibeRails/wwwroot/js/modules/vibe-terminal.js` — `attachClipboardPaste`

### ✅ Clicking a tab auto-reconnected the terminal

Tab button click passed `connectIfNeeded: true`, making tab selection act as an implicit
reconnect. Fixed by changing all tab activation calls in `addLocalTab()` and `restoreTabs()` to
`connectIfNeeded: false`. Reconnect is now explicit only (Reconnect button, or
`reconnectActiveTab()`).

### ✅ Unselected tabs appeared offline during navigation

Navigation destroyed all browser sockets. Only the active tab reconnected, making inactive tabs
look offline. Resolved as a side effect of the `connectIfNeeded: false` change above. Tabs now
correctly show as paused/disconnected rather than silently re-connecting on activation.

### ✅ History lost on reconnect / hard refresh

Previously used `CircularBuffer` with ANSI break-point heuristics (`\x1b[?1049h`, `\x1b[2J`,
`\x1bc`) to find a "clean" restart point in raw PTY bytes. This was fragile — short sessions
or plain-shell sessions often had no break point, giving clients a blank or partial screen.

**Fix:** replaced `CircularBuffer` entirely with the `TerminalEmulator` library. Every PTY
output chunk is fed to an in-memory VT100 state machine. On reconnect, `GetGridReplay()`
serializes the full scrollback history + current screen as ANSI and sends it as a single
binary WebSocket frame. xterm.js renders it instantly — no animation, no DB, no break-point
guessing. The client always gets the complete scroll history, exactly as VS Code does.

See **TerminalEmulator Integration** section below for architecture details.

### ✅ AI TUI double-render / stale cells on reconnect and resize

Mitigated by:
- pre-resize + replay-before-subscribe + immediate post-replay redraw on reconnect
- `resetDisplayOnly()` in the shrink-only resize path clears stale xterm cells before a real PTY geometry change
- manager generation guards prevent stale async init from completing after navigation

### ✅ Cursor stuck at bottom-right and not blinking after replay

After reconnect the cursor appeared frozen at the bottom-right corner of the xterm.js viewport
and cursor blink was disabled.

**Root cause:** `\u001bc` (RIS hard reset) resets xterm.js cursor state to its defaults —
visible but **not blinking**. TUI apps normally re-enable blink via `\x1b[?12h` on startup, but
those sequences are ephemeral and not captured in the emulator cell grid, so they are never
replayed.

**Original fix (2026-03-16):** appended `?25h` + `?12h` after CUP. **Superseded 2026-03-17**
by the ghost-cursor fix — `?25h` was causing a second ghost cursor when TUIs drew their own
block and the real cursor was unexpectedly restored. Both `?25h` and `?12h` removed. The Ctrl+L
redraw now handles all cursor state restoration correctly.

Key file: `VibeRails/Services/Terminal/TerminalGridSerializer.cs` — end of `Serialize()`

### ✅ Double print on remote viewer connect + local title OSC wrong path

**a) Double print:** `RemoteOutputConsumer` streamed live PTY bytes concurrently while
`GetGridReplay()` snapshot was being sent, so the browser got live output interleaved with
the full replay. Fixed by adding a `replayInProgress` volatile int — `canForward()` returns
false while replay is in flight. Applied to both the replay path and PIN-verified path.

**b) OSC title via wrong path:** `NotifyRemoteTakeoverAsync` wrote the title OSC to PTY stdin
via `WriteBytesAsync`. ConPTY does not interpret OSC from stdin — it passes them to the shell
as raw input. Fixed by adding `Terminal.PublishOutput()` which dispatches bytes to all
`ITerminalConsumer`s via the output path (same as PTY-produced bytes). Title sequences now
use that instead. Also corrected `\x1b` → `\u001b` escapes throughout.

Key files:
- `VibeRails/Services/Terminal/Terminal.cs` — new `PublishOutput` method
- `VibeRails/Services/Terminal/TerminalRunner.cs` — `replayInProgress` gate,
  `PublishOutput` in `NotifyRemoteTakeoverAsync` / `HandleRemoteBrowserDisconnectedAsync`

### ✅ Remote viewer connect/disconnect not visible on native CLI

Superseded by the native CLI remote alerting deferred issue above. The OSC title approach was
implemented (via `PublishOutput`) but the title gets overwritten immediately by the TUI/shell.
Remote access for native CLI sessions has been disabled pending a proper alerting layer.

Key files: `VibeRails/Services/Terminal/TerminalRunner.cs` —
`_nativeRemoteEnabled`, `ShouldEnableRemote`, `isNativeCli` parameter on `CreateSessionAsync`

### ✅ Garbled character (Ƽ) at start of remote viewer replay

The first character rendered in the remote viewer on reconnect was `Ƽ` (U+01BC) instead of a
clean screen reset.

**Root cause:** `TerminalGridSerializer.cs` emitted `"\x1bc"` as the hard-reset sequence. In
C#, `\x` greedily consumes hex digits, so `\x1bc` is codepoint `0x1BC` (Ƽ), not ESC + `c`.

**Fix:** `"\x1bc"` → `"\u001bc"` in `TerminalGridSerializer.cs:32`.

Key file: `VibeRails/Services/Terminal/TerminalGridSerializer.cs` — `Serialize()`

### ✅ Native CLI showed only a blinking cursor in remote browser until resize

**Root cause:** premature `fitAndSyncTerminal({ force: true })` call in `socket.onopen` fired
before the terminal panel CSS had settled, sending wrong cols/rows to the PTY.

**Fix:** removed the premature call. `scheduleViewportLayoutSync(40ms)` fires after layout
settles and sends the correct resize. The Ctrl+L fallback in `HandleRemoteReplayRequestAsync`
has been removed — `GetGridReplay()` always returns a valid full-screen state, so no fallback
is needed. The PIN-verified path also now uses `GetGridReplay()` instead of Ctrl+L.

Key files:
- `VibeRailsFrontEnd/.../Views/Terminals/Index.cshtml` — `socket.onopen`
- `VibeRails/Services/Terminal/TerminalRunner.cs` — `HandleRemoteReplayRequestAsync`

---

## TerminalEmulator Integration

`TerminalEmulator` (`C:\source\VibeControl2\TerminalEmulator\`) is an AOT-safe, net10.0 VT100
state machine that replaced `CircularBuffer` as the terminal state proxy.

**What it does:**
- Parses all ANSI/VT100 sequences (CSI, OSC, DCS, SGR, alternate screen, 256-color, true color)
- Maintains a 2D cell grid (`TerminalCell[rows, cols]`) for the current visible screen
- Keeps a scrollback ring buffer (1000 rows default) of rows that have scrolled off
- Tracks cursor position, SGR attributes, and alternate screen state

**How it's wired:**
- `TerminalEmulatorConsumer` (`ITerminalConsumer`) subscribes to every PTY output chunk and
  feeds it to the emulator under `_emulatorLock`
- `Terminal.Resize()` also resizes the emulator to keep dimensions in sync
- `Terminal.GetGridReplay()` snapshots scrollback + screen under lock, then calls
  `TerminalGridSerializer.Serialize()` outside the lock
- reconnect/reattach now uses emulator replay for both plain shells and managed AI CLIs; attach
  correctness comes from pre-resize + snapshot-before-subscribe + post-replay redraw, not from a
  CLI-type-specific replay bypass

**`TerminalGridSerializer.Serialize()`:**
- Hides cursor (`?25l`), then soft-clears screen (`ED2`) + scrollback (`ED3`) + homes cursor (`CUP 1,1`)
  — does NOT use `ESC c` (RIS) which resets terminal modes and fights TUI cursor state
- Writes scrollback rows oldest-first with `\r\n`, using delta SGR encoding (pushes into xterm scrollback)
- Writes each screen row prefixed with `\x1b[{r+1};1H` (absolute CUP per row, prevents drift from
  wide chars / full-width columns / wrap semantics)
- Resets SGR and repositions cursor via CUP to the emulator's real cursor position
- Does NOT restore cursor visibility — leaves cursor hidden so Ctrl+L redraw lets the TUI
  re-establish its own cursor state (avoids ghost cursor from real + TUI fake cursors both visible)
- Returns UTF-8 bytes ready for a binary WebSocket frame

**Thread safety:**
- `_emulatorLock` (C# 13 `Lock`) serializes `Write()` and `Resize()` from concurrent threads
- `GetSnapshot()` and `GetScrollback()` return copies — serialization is lock-free

**Key files:**
- `TerminalEmulator/Terminal.cs` — public API (`Write`, `Resize`, `GetSnapshot`, `GetScrollback`)
- `TerminalEmulator/TerminalBuffer.cs` — grid + scrollback state
- `TerminalEmulator/AnsiParser.cs` — VT100 state machine
- `VibeRails/Services/Terminal/Terminal.cs` — `GetGridReplay()`
- `VibeRails/Services/Terminal/TerminalGridSerializer.cs` — ANSI serializer
- `VibeRails/Services/Terminal/TerminalEmulatorConsumer.cs` — feeds PTY bytes to emulator

---

## Notes

- Terminal tracking is consolidated in this root file. The duplicate `VibeRails/TERMINAL.md`
  investigation file was removed on 2026-03-12.
- Do not reintroduce `CircularBuffer` or raw replay as the reconnect baseline — fully replaced
  by the TerminalEmulator grid approach.
- Replay is now the standard reconnect baseline for all session types. Do not reintroduce
  CLI-specific skip-replay branches unless a concrete reproducer and regression tests require it.
- **Sluggish typing (accepted):** ~20 ms per character echo latency is inherent — xterm.js v6
  `WriteBuffer` batches via `setTimeout(0)` (~4 ms) + rAF render (~16 ms). No delay on our
  `onData` → `socket.send()` path; the bottleneck is xterm.js's async write pipeline. Fixing
  would require local echo or xterm.js internal APIs. Accepted as-is.
