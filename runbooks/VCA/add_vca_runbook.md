# Adding a VCA Rule

Use this runbook for every new rule exposed by the VibeRails Rules UI and enforced by Git Guard.
It is deliberately a definition-of-done checklist: adding an enum or dropdown row alone does not
create a working VCA rule.

## Inputs

Before editing, write down:

- Canonical display/syntax, including any parameters.
- What constitutes a pass and a violation.
- Which Git changes count: add, modify, delete, rename, copy, or only a subset.
- Path scope: repository root, declaring AGENTS.md directory, or another explicit base.
- WARN, COMMIT, and STOP behavior. Normally all three use the shared enforcement pipeline.
- Whether the rule runs at pre-commit or must be deferred to commit-msg.
- What an invalid or unevaluable rule reports. Do not silently pass a recognized rule.

If any item materially changes the meaning of the rule, resolve it with the requester first.

## Architecture to preserve

The production path is:

```text
AGENTS.md
  -> AgentRuleSectionReader (one discovery contract)
  -> RulesTool.ValidateVcaReportAsync (active validator)
  -> VcaPreflightStep / Git hooks / validate_vca MCP tool
  -> WARN, COMMIT acknowledgment, or STOP
```

The Rules page's **Validate** action uses `RuleValidationService`. The modular
`Services/VCA/ValidationService` + `ValidatorList` path is legacy, but remains registered and
tested. A new rule should cover all three unless that legacy layer fundamentally cannot represent
the rule; document any deliberate omission.

`AgentRuleSectionReader` remains the only rule-discovery implementation. Never add a second regex
that scans AGENTS.md independently. Fenced examples, legacy headings, and section boundaries must
continue to mean the same thing to the UI and Git hooks.

## Implementation checklist

### 1. Catalog and parse the rule

Edit `VibeRails/Services/RulesService.cs`:

- Add a `Rule` enum value.
- Add its canonical UI name to `_keyValuePairs`.
- Add a user-facing description to `_descriptions`.
- Update `RuleParser.TryParse` for parameterized syntax. Exact static rules should not gain an
  overly broad prefix match.
- For non-trivial parameter parsing, put one shared parser/value object in `Services/VCA/` and use
  it from every validator and writer.

Add catalog/parser tests. Verify both the template returned by `/api/v1/rules/details` and a real,
fully populated rule line.

### 2. Check discovery compatibility

Confirm that the complete line survives `AgentRuleSectionReader`:

```markdown
## Vibe Rails Rules
- My parameterized rule('value') (STOP)
```

The final `(WARN|COMMIT|STOP)` suffix belongs to VCA; parentheses inside the rule text must remain
part of `RuleText`. Add a discovery test if the new syntax stresses the Markdown parser.

### 3. Validate writes

Edit `AgentFileService` when syntax needs contextual validation:

- `CreateAgentFileAsync` covers the creation wizard.
- `AddRulesAsync` covers bare/default-WARN additions.
- `AddRuleWithEnforcementAsync` covers normal UI additions.
- Reject unsafe or malformed parameters before writing the file.
- Keep mutations aligned with `AgentRuleSectionReader.ParseDocument`; do not locate headings with
  `IndexOf` or an independent rules-section scan.

The POST/PUT/DELETE routes already return the updated agent. Preserve that response contract.

### 4. Implement active Git Guard and MCP enforcement

Edit `VibeRails/Services/Mcp/Tools/RulesTool.cs`, inside `ValidateRuleAsync` or a focused helper:

- Evaluate `GitStagedSnapshot`, never unstaged working-tree contents during a commit hook.
- Use the files already scoped by the declaring AGENTS.md.
- Consider deleted paths (`ExistsInIndex == false`).
- For rename-sensitive rules, check both `RelativePath` and `PreviousRelativePath`.
- Return `Pass`, `Violation`, `Deferred`, or `Unrecognized` accurately.
- A recognized but unevaluable rule should return a clear `UNSUPPORTED:` message. Choose the
  state by asking what the failure means for the staged files. Missing *input* for a real check
  (coverage with no report) is a `Violation`, so the declared level still gates. A malformed
  *argument* that leaves the rule with nothing to compare against (a path lock whose path will
  not parse or resolve) is `Unrecognized` — a violation there fires on every commit regardless
  of what changed, and at `STOP` blocks all work until `AGENTS.md` is hand-edited. Validate
  argument syntax on the write path instead, where the user can still fix it.
- Do not implement WARN/COMMIT/STOP branching in the validator. Return one violation and let the
  shared finding pipeline apply enforcement consistently.
- Include actionable target paths in the reason.

If a rename can cross an AGENTS.md scope boundary, ensure `GetScopedFiles` considers the previous
path as well as the new path.

### 5. Implement Rules-page validation

Edit `VibeRails/Services/RuleValidationService.cs`:

- Add the enum case to both `ValidateAsync` and `ValidateWithSourceAsync` when applicable.
- Prefer `ValidateWithSourceAsync` semantics; it knows the declaring AGENTS.md.
- Return affected files for UI evidence.
- Use the same shared parameter parser and path-resolution helper as Git Guard.

### 6. Keep the modular validator path coherent

For a per-file rule:

- Add `Services/VCA/Validators/<RuleName>Validator.cs` implementing `IRuleValidator`.
- Register it in `ValidatorList`.
- Register it in DI only if the validator has injected dependencies.
- Add a validator/registry test.

### 7. Make parameterized rules usable in every Rules UI flow

Static rules need no special frontend work. Parameterized rules do. Update
`VibeRails/wwwroot/js/modules/agent-controller.js` in all three flows:

1. Inline Rule files editor (`showInlineAddRule`).
2. Full agent editor (`addRule` before `showEnforcementPicker`).
3. New AGENTS.md wizard (`renderStep2Rules` and its selected-rule state).

Requirements:

- Show the catalog template, then collect the real parameter before choosing enforcement.
- Send canonical rule text to the API.
- Keep the template available after one instance is added when multiple instances are valid.
- Restore entered parameters when navigating backward in the creation wizard.
- Escape every value written into HTML.
- Validate obvious input errors client-side, while keeping the server authoritative.
- Add focused Node tests for formatting and validation helpers.

### 8. Update documentation

- Update the rule count and list in the repository `AGENTS.md`.
- Update `VibeRails/Services/VCA/AGENTS.md` for new discovery/enforcement semantics.
- Add any unusual operational notes to this runbook.

## Test matrix

At minimum, cover:

| Layer | Required cases |
| --- | --- |
| Catalog/parser | Template listed; real syntax recognized; malformed syntax rejected |
| Discovery | Rule text and enforcement suffix split correctly |
| Writer | Valid rule is written; unsafe contextual parameter is rejected |
| Active `RulesTool` | Pass and violation; WARN and STOP/COMMIT routing; nested AGENTS scope |
| Git change semantics | Every applicable add/modify/delete/rename case |
| Paths | Slash normalization, exact boundary behavior, case policy, escape rejection |
| Rules-page validator | Affected files and pass result |
| Legacy validator | Correct registry mapping and per-file result |
| Frontend | Template becomes canonical populated text; invalid parameter is rejected |
| End to end | A throwaway real Git repository and real pre-commit host block the violation |

Prefer a table-driven unit test for a change-kind matrix and at least one real-hook test. Unit-only
coverage can miss staged deletion and rename behavior.

## Verification commands

Run focused tests while iterating, then the complete suite:

```powershell
dotnet test Tests\Tests.csproj --artifacts-path .codex-test-artifacts --filter FullyQualifiedName~<RuleName>
node --check VibeRails\wwwroot\js\modules\agent-controller.js
node --test Tests\wwwroot\js\agent-controller.test.mjs
dotnet test Tests\Tests.csproj --no-restore --artifacts-path .codex-test-artifacts
git diff --check
```

If an in-app browser is available, verify the inline editor, full editor, and creation wizard in
the same hosted surface VS Code embeds.

## Worked contract: path locks

The current path-lock rules are the reference implementation for parameterized, change-kind-aware
rules:

```markdown
- File Lock('config/settings.json') (WARN)
- Directory Lock('generated') (STOP)
```

- Paths are relative to the directory containing the declaring AGENTS.md.
- Absolute paths and `..` escapes are rejected.
- File Lock matches one exact Git path.
- Directory Lock matches the directory path and every descendant, with a real path boundary.
- Additions, modifications, deletions, and both sides of renames count.
- The declaring AGENTS.md is excluded from its own lock so the policy can be changed or removed.
- Other AGENTS.md files inside a locked directory are ordinary locked content.
- Targets do not need to exist when the rule is created; the rule can prevent a future addition.

## Common incomplete implementations

- Adding only the enum/catalog row: visible but unenforced.
- Adding only a legacy `IRuleValidator`: tests pass while Git Guard ignores it.
- Validating only files that still exist: deletions bypass the rule.
- Checking only a rename destination: renaming a protected file out bypasses the rule.
- Treating a directory as a string prefix: `src/locked-old` falsely matches `src/locked`.
- Letting the UI submit a placeholder template literally.
- Filtering a parameterized catalog entry after its first instance, preventing multiple targets.
- Parsing AGENTS.md with a new regex and reintroducing UI/hook disagreement.
- Silently passing malformed recognized rules.

## Definition of done

A rule is complete only when it is discoverable, safely writable, configurable in every Rules UI
flow, enforced by the active staged-index pipeline, represented in Rules-page validation, covered
by focused and real-hook tests, documented, and green in the full test suite.

For a future request, the user can point to this file and provide only the rule syntax and desired
pass/violation behavior. Use the Inputs section to identify any genuinely missing semantic choice,
then execute the checklist without requiring the user to enumerate implementation files.
