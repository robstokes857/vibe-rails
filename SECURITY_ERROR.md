# Unresolved security review findings

## VB-ELC4K-180 / VIBE-108: CodeQL findings — fixes awaiting owner review

Recorded 2026-10-06. GitHub alerts #17–27 and #31–35 identify rule-file routes/services
that used request paths before checking repository membership. Authenticated callers could
read/write rule-shaped content outside the current repository or through a linked file.
`RuleFilePath` now enforces repository containment, the `vc.rules.md` filename, and rejection
of linked components/Git metadata before route metadata or service I/O. Creation uses
CreateNew instead of an overwriting write. Tests use disposable files, never application data.
Filesystem validation does not isolate the process from a concurrent local link-swap attacker.

Alert #38 flags the Windows launch working directory. Although this value is not shell text,
the bare `pwsh` executable with ShellExecute could be resolved in that chosen directory.
The launcher now resolves `pwsh.exe` to an absolute path using only absolute PATH entries
before handing the working directory to ShellExecute.

Alerts #39–42: MCP log fields could introduce forged lines/control sequences. The owner
explicitly requested retaining full tool names, error payloads and exception details, accepting
their possible sensitive content. Those values are now losslessly control-escaped; they are
not removed, redacted or truncated. See the specific diagnostic exception in API_SEC.md.

The remaining alerts and validation/dispositions are recorded in the card's Comments.
Review these fixes before removing this entry.

## VB-8WE2S-129 / F1: Automation saves discard Worker launch restrictions

Recorded 2026-10-05 against the uncommitted working tree on
`336373c0c51b2dc68abb3350910cee533502651a` (`origin/main`). Severity: high.
The issue belongs to the shared Automation editor changes; the card's prompt fallback
change has no finding.

Saving an existing Automation always calls the shared Worker editor's `save()` in
`VibeRails/wwwroot/js/modules/jobs-controller.js:1403`. The first save always rewrites
the Environment in `VibeRails/wwwroot/js/modules/environment-editor.js:199`, even
when only the Automation name, schedule or other Job metadata changed. Managed
argument readers/builders retain only the arguments represented by their controls.
Other stored native arguments disappear from the persisted `CustomArgs`.

Confirmed using the production frontend in Chromium with disposable intercepted API
fixtures, without opening application databases or launching an LLM:

1. Give the existing Codex Worker these arguments:
   `--model gpt-5.6-sol -c model_reasoning_effort=high --sandbox read-only --search`.
2. Open its Automation, change only the Automation name, and save.
3. The frontend issues an Environment PUT with
   `--model gpt-5.6-sol -c model_reasoning_effort=high`, followed by the Job PUT.
   No Worker control was edited.

The next launch no longer carries the explicit read-only sandbox restriction and
instead depends on other provider configuration/defaults. Other Automations and Board
launches sharing that Environment also observe the changed arguments. The backend
consumes stored `CustomArgs` as launch argv (`VibeRails/Routes/TerminalRoutes.cs:83`).
This review did not launch a real provider or demonstrate a subsequent filesystem write.

Hold merge of the broader Automation editor changes. Metadata-only Job saves must
preserve the Worker unchanged, and supported Worker edits must retain unrepresented
native arguments. Verify both with browser round-trip coverage before closing this
finding. VB-8WE2S-129 is flagged for the owner's review; full findings and validation
are saved in its Comments.

### Resolution (VB-AIL1B-130 / VIBE-58, 2026-10-05) — fixed, awaiting owner review

- `environment-editor.js` takes an existing Worker's loaded form as its write baseline, so a
  save that changed no Worker control sends neither the Environment PUT nor the settings PUT.
- `environment-controller.js`: Codex and Claude now load flags without a control into an
  **Additional Arguments** field (as Antigravity/Copilot/Grok/OpenCode already did) and append
  them on rebuild; an unlisted saved effort stays selected as `(custom)`.
- Browser round trip (`UITests/tests/jobs-environment.spec.js`, "a rename leaves the Worker
  untouched…"): the reproduction above now makes one Job PUT and no Worker write; changing the
  model then PUTs `--model gpt-5.6-luna -c model_reasoning_effort=high --sandbox read-only --search`;
  an unchanged Environments-page save writes nothing. With the baseline removed the test fails
  on the rename's Worker PUT. Node round trips: `Tests/wwwroot/js/environment-argument-preservation.test.mjs`.

Delete this entry once the owner has reviewed the fix.
