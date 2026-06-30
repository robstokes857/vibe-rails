# Rogue approval across Codex sessions plus frozen/stale TUI after interrupt

## Summary

An interrupted Codex task appeared to continue or resume modifying files after its TUI became stale/unresponsive. The most concerning part is the approval flow: approving commands in a second Codex session appeared temporally correlated with previously interrupted/background work from the first session continuing.

This looks like a possible cross-session approval or task-isolation bug, with a frozen/stale TUI making the behavior hard to detect.

## Environment

- Codex CLI: `v0.142.4`
- OS: Windows
- Shell: PowerShell
- Terminal: PTY-backed TUI
- Multiple Codex sessions open concurrently in the same repo

## High-Level Scenario

1. Session A was running a coding task.
2. I pressed Escape to interrupt/stop Session A.
3. Session A's TUI stopped updating and became effectively non-interactive.
4. Files continued changing after Session A looked stopped/frozen.
5. I opened Session B for a different task.
6. Session B asked for approvals for GitHub/PR commands.
7. After approving commands in Session B, previously interrupted work from Session A appeared to continue/resume.

## Primary Concern

Approval may not be fully scoped to the session/task that requested it.

If approval in Session B can unblock or affect Session A, then a user may believe they are approving a command for the visible/current session while another interrupted/background task receives the approval and continues modifying code.

## Timeline Evidence

All times UTC.

```text
11:44:53.613  Session A prompt submitted
11:45:04.xxx  Session A TUI starts showing suspicious mixed repaint/output
11:45:11.096  Last normal Session A output chunk
11:45:11-11:51:21  Session A produces no normal updates
11:45:14.615  Session B starts
11:46:04.480  Session B PR prompt submitted
11:47:11.223  Session B approval: git push
11:47:20.535  Session B approval: gh pr view
11:50:02.080  Session B approval: gh pr create
11:50:02.333  First observed dirty-file mtime after approvals
11:51:21+     Reconnect/redraw output appears, mostly stale transcript repaint
```

The first TUI stall happened before the later approvals. However, file modification timing lined up closely with the approval period, especially immediately after `gh pr create` approval.

## Suspicious TUI Output Before Freeze

Right before Session A stopped updating, the TUI showed normal Codex status mixed with large command output fragments inside spinner/status repaint areas.

Example sanitized visible output:

```text
• Ran git status --short
  └ (no output)

• Ran Get-Content <repo-doc-file>
  └ # MCP Server (in-process)

  ... +134 lines (ctrl + t to view transcript)
  (`EnableAotAnalyzer`/`EnableTrimAnalyzer`) reporting zero warnings.
  Avoid `WithToolsFromAssembly()`
  (reflection scan) - it is the AOT-unsafe variant.

• Ran rg -n "MCP|Mcp|mcp|model context|ModelContext|session|opt" <repo> Tests
  └ <repo-file>:77:
     <repo-file>:92:
  ... +806 lines (ctrl + t to view transcript)

  E.push("}"),E.join(r)+r}}function sU(e,t){let r={},i=Xx().optionsNameMap;for(let o in e)...

• Working (17s · esc to interrupt)

› Write tests for @filename
```

The suspicious part is that large `rg` output / minified JavaScript fragments appeared interleaved with the TUI repaint/spinner area. Shortly after this, the session stopped producing normal updates.

## Byte Stream Evidence

Local PTY byte-stream inspection did not show an obvious malformed terminal-control sequence from the host side.

At the point where Session A stopped normally updating:

```text
last normal output chunk timestamp: 11:45:11.096
next output chunk timestamp:       11:51:21.152
gap:                               ~370 seconds
```

The final normal chunks were small Codex repaint/control fragments, ending with balanced synchronized-output markers and cursor visibility restored.

Representative escaped byte fragments:

```text
ESC[?2026h ESC[0 q
... spinner/status repaint bytes ...
ESC[?25h
ESC[?2026l
ESC[?25l ESC[23;3H ESC[?25h
```

Terminal mode checks around the freeze:

```text
incomplete escape tail: no
alternate screen left active: no
mouse mode left active: no
cursor visibility final state: visible
synchronized output ?2026 set/reset: balanced
```

Later suspicious output after reconnects looked like full-screen redraws of old transcript content rather than fresh execution. Those redraw chunks started with cursor positioning and line repaint sequences like:

```text
ESC[?25l ESC[25;1H
ESC[1m ESC[2m ESC[20;1H ...
```

So reconnects may have replayed stale transcript lines such as:

```text
✔ You approved codex to always run commands that start with git push
• Ran git push -u origin <branch>

✔ You approved codex to always run commands that start with gh pr view ...
• Ran gh pr view ...

✔ You approved codex to always run commands that start with gh pr create
• Ran gh pr create ...
└ <PR URL>
```

These may have been redraws, not actual re-execution, but the UI made that hard to distinguish.

## Approval Flow Evidence

Session B displayed approval prompts like:

```text
Would you like to run the following command?
Environment: local
Reason: Do you want to push the local branch to GitHub so I can open the PR?

$ git push -u origin <branch>

✔ You approved codex to always run commands that start with git push
• Running git push -u origin <branch>
```

Then:

```text
Would you like to run the following command?
Environment: local
Reason: Do you want to check GitHub for an existing PR for this branch before creating one?

$ gh pr view <branch> --repo <owner>/<repo> --json number,url,title,state

✔ You approved codex to always run commands that start with gh pr view ...
• Running gh pr view ...
```

Then:

```text
✔ You approved codex to always run commands that start with gh pr create
• Running gh pr create ...
```

Around this approval window, additional file modifications appeared that seemed related to the interrupted Session A task, not the visible PR task in Session B.

## Expected Behavior

- Approval should be scoped to the exact session/task that requested it.
- Approval in one Codex session should never unblock work in another session.
- Escape should cleanly interrupt active model/tool work.
- Interrupted tasks should not continue modifying files unless explicitly resumed.
- If work is still running, the TUI should clearly show that state.
- Reconnect/redraw output should be distinguishable from fresh command execution.

## Actual Behavior

- Session A's TUI became stale/unresponsive after interrupt.
- User input after the freeze did not visibly echo or produce a response.
- Files continued changing after Session A appeared interrupted.
- Session B approvals appeared temporally correlated with additional/background edits.
- Reconnects replayed stale transcript content, making it hard to tell whether commands were actually running again.

## Requested Investigation

Please investigate:

- Whether command approvals are scoped per Codex session/task
- Whether approval in one session can unblock another interrupted/background task
- Whether Escape cancellation fully stops active tool/model work
- Whether interrupted tasks can keep modifying files after the TUI freezes
- Whether concurrent Codex sessions in the same repo can affect each other
- How users can identify and kill background Codex work after interruption
- How stale redraws can be distinguished from fresh command execution
