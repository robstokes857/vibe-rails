# VCA — Vibe Control Automation

VCA is the rule engine behind Git Guard. It answers one question: **given a set of changed files,
which rules in the repository's `vc.rules.md` files apply, and does anything about this change
violate them?**

Rules are declared in Markdown, by humans, in `vc.rules.md` files. The file may also carry
ordinary documentation (the `## Files` section), and it may contain fenced examples of rules —
most of what follows exists to keep that documentation from becoming policy by accident.

## Who asks

Three callers, one engine:

```
Rules page (browser)              Git hooks (pre-commit, commit-msg)        MCP (validate_vca)
        │                                      │                                   │
 AgentRoutes                          VcaHookRunner → GitPreflightPipeline    RulesTool.ValidateVca
        │                                      │                                   │
 AgentFileService ────┐                 VcaPreflightStep                            │
                      │                        │                                   │
                      │                 VcaHookValidationService.ValidateAsync      │
                      │                        │                                   │
                      │                 RulesTool.ValidateVcaReportAsync ───────────┘
                      │                        │
                      └──────► AgentRuleSectionReader ◄──────┘
                                 (rule discovery — one implementation)
```

`AgentRuleSectionReader` is the only thing that decides what a file declares. It is shared on
purpose; see [Why discovery is shared](#why-discovery-is-shared). Rules-page working-tree
validation (`RuleValidationService` in `../RuleValidationService.cs`) is a separate step that
validates already-discovered rules — it does not call `AgentRuleSectionReader` itself (the
endpoint that invokes it calls `AgentFileService.GetRulesWithEnforcementAsync` for discovery).
`ValidationService` / `FileAndRuleParser` in this directory are legacy and no longer used in
production.

## The rule-discovery contract

A rule is a Markdown list item, inside a rules section, outside a code fence.

### 1. Rules live under a rules heading

| Heading | Status |
| --- | --- |
| `## Vibe Rails Rules` | **Canonical.** What `AgentFileService` writes and what new files get. |
| `## Vibe Control Rules` | Legacy. Still read, for files written before the canonical heading. |
| anything else | Not a rules section. |

`## Rules` is deliberately **not** accepted. It is far too common a heading in ordinary
documentation to treat as enforceable policy.

A file may contain more than one rules section; all of them are read.

### 2. A section ends at the next heading

Any Markdown heading (`#`, `##`, `###`, …) closes the section. This is what keeps a `## Files`
list from being read as rules.

### 3. Fenced code blocks are never rules

Anything between ` ``` ` or `~~~` delimiters is skipped — **including inside a rules section**.
Documentation that shows what a rule looks like is documentation. This is not a nicety; see below.

### 4. Three accepted forms

```markdown
## Vibe Rails Rules
- [STOP] Log all file changes      ← bracket form
- Log all file changes (STOP)      ← suffix form
- Log all file changes             ← bare form, means WARN
```

The bare form matters: `AgentFileService.AddRulesAsync` writes bare `- {rule}` lines, so a reader
that required an explicit level would ignore rules the Rules page had just added.

Enforcement tokens are case-insensitive: `WARN`, `COMMIT`, `STOP`, `SKIP`, `DISABLED`.

### 5. Scope

A rule applies to changed files under the directory of the `vc.rules.md` that declares it. A nested
`vc.rules.md` governs its own subtree and nothing above it (`GetScopedFiles`). Rules with no changed
files in scope are not evaluated and do not count toward "applicable rule(s)".

Duplicate rules from the same source file are collapsed (`RulesTool.AddRuleIfNew`).

### 6. Parameterized path locks

`File Lock('path/to/file')` protects one exact Git path. `Directory Lock('path/to/directory')`
protects the directory and every descendant. Paths are relative to the directory containing the
declaring vc.rules.md; absolute paths and `..` escapes are invalid. Additions, modifications,
deletions, and both sides of renames count as changes. Matching uses Git path boundaries, so a lock
on `src/locked` does not match `src/locked-old`.

The declaring vc.rules.md is excluded from its own lock. This is the control-plane escape hatch that
keeps a STOP rule editable and removable; other vc.rules.md files beneath a directory lock remain
ordinary protected content.

## Enforcement levels

| Level | Pre-commit | Commit-msg | Meaning |
| --- | --- | --- | --- |
| `WARN` | reported, allows commit | — | Visible, never blocking. |
| `COMMIT` | reported, allows commit | **blocks** until acknowledged | Needs `[VCA:<source>:<slug>] Reason: …` in the commit message. |
| `STOP` | **blocks** | blocks | Cannot be acknowledged or bypassed. |
| `SKIP` / `DISABLED` | not evaluated | not evaluated | An explicit opt-out. |

`COMMIT` is deliberately non-blocking at pre-commit: the acknowledgment lives in a commit message
that does not exist yet. The commit-msg hook is where it is enforced.

## When a rule cannot be evaluated

Three different failures, deliberately treated differently:

**Recognized but unevaluable** — a validator exists and refuses to guess. The coverage rules are
the live example: the hook has no coverage report for a staged snapshot, so it reports
`UNSUPPORTED:` rather than silently passing. This honors the declared enforcement level, so a
`STOP` coverage rule blocks. That is intended: the user asked for a gate, and pretending a gate
passed is worse than blocking.

**Unrecognized** — no validator matches the rule text at all
(`RuleValidationState.Unrecognized`). This says nothing about the staged code; it says the rule is
unenforceable as written. It is always reported as a **warning regardless of declared level**.
Blocking a commit over a typo in `vc.rules.md` would enforce nothing while costing the user the
commit, and the level on the line describes a check that is not running.

**Recognized but with an unusable argument** — the path locks are the live example. A lock whose
path will not parse, escapes the declaring directory, or escapes the repository has no target to
compare the staged files against. That is not the coverage case: a coverage rule with no report
still describes a real gate over the staged files, while a lock with no path describes nothing,
so a violation would fire on *every* commit regardless of what changed. At `STOP` that wedges all
work until `vc.rules.md` is hand-edited — possibly the very file the lock covers. These report
`UNSUPPORTED:` through `RuleValidationState.Unrecognized`, so they warn and never block.

The write path is where lock syntax is actually enforced: `AgentFileService` rejects
lock-shaped text it cannot parse, so the UI cannot author a lock that will only warn later. A
lock pointing at a path that does not exist yet is legal — it simply never matches until
something at that path is staged.

All three are surfaced. None is silently swallowed — a rule that quietly does nothing is worse
than one that complains.

## Why discovery is shared

There used to be two readers with different contracts, and they disagreed in every direction:

- **The Rules page** (`AgentFileService`) required the exact `## Vibe Rails Rules` heading, stopped
  at the next `##`, and dropped rule text it did not recognize.
- **The Git hook** (`RulesTool.ParseRules`) regex-scanned the **entire file**, with no concept of
  headings, sections, or code fences.

On this repository's own rules file (then `AGENTS.md`, before the `vc.rules.md` rename) that
produced a commit that could not be made and could not be explained. A fenced example under `**Rule Format**:` —

````markdown
```markdown
## Rules
- Cyclomatic complexity < 20 (COMMIT)
- Require test coverage minimum 80% (STOP)
- Log all file changes (WARN)
```
````

— was read by the hook as three live rules. The `STOP` one demanded a coverage report the hook
cannot produce, so it blocked, and `STOP` cannot be acknowledged. Meanwhile the Rules page showed
**"No agent files have rules yet"**, because its reader wanted a heading the file did not have. A
second fenced example in a "how to add a rule" tutorial contributed a fourth phantom rule.

Any future change here must preserve the property that made that debuggable only in hindsight:
**the Rules page can always account for what the hook does**, because they ask the same reader.
Splitting them again — even "temporarily", even for one caller — reintroduces the whole class.

The corresponding rule for the other direction: writes stay validated. `AddRulesAsync` and
`AddRuleWithEnforcementAsync` still refuse rule text `RulesService` does not know, so the UI cannot
author junk. Reads no longer filter, so hand-edited junk is shown rather than hidden.

## Layout

| Path | Role |
| --- | --- |
| `AgentRuleSectionReader.cs` | Rule discovery. The contract above, and nothing else. |
| `Validators/` | Per-rule validators used by the older `ValidationService` path. |
| `ValidationService.cs`, `FileAndRuleParser.cs` | Legacy working-tree validation (registered in DI, unused in production — only exercised by `ValidationServiceIntegrationTests.cs`). |
| `../RuleValidationService.cs` | Active working-tree validation for the Rules page (`/api/v1/agents/validate`). |
| `Hooks/` | Git-hook host, runner, console presenter. See `Hooks/` for the console UI. |
| `../Mcp/Tools/RulesTool.cs` | `ValidateVcaReportAsync` — the validation the hooks actually run. |
| `../AgentFileService.cs` | Rules-page CRUD over `vc.rules.md` files. |
| `PathLockRule.cs` | Shared File Lock / Directory Lock syntax, safe path resolution, and matching. |
| `../GitPreflight/` | Step pipeline (VCA → MintLint → automated workflows) and its event stream. |

## Testing

| Area | Tests |
| --- | --- |
| Discovery contract | `Tests/Services/VCA/AgentRuleSectionReaderTests.cs` |
| Validation + parsing | `Tests/Services/Mcp/RulesToolTests.cs` |
| Real hooks, real repos | `Tests/Services/VCA/VcaHookEndToEndTests.cs` |
| Rules-page CRUD | `Tests/AgentFileServiceTests.cs` |

`VcaHookEndToEndTests` builds throwaway Git repositories and runs the real hook host against them.
Prefer adding cases there for anything that touches what blocks a commit — it is the only layer
that exercises staging, scoping, and exit codes together.

## Hook removal

Removing Git Guard strips only the VibeRails-managed sections from `pre-commit`, `commit-msg`, and
`post-commit`; third-party hook content and preserved sidecars are restored. A repository-local
`.git/.viberails-git-guard-disabled` marker prevents startup auto-install from putting the hooks
back. An explicit Install or Repair action removes that marker and re-enables startup installation.

---

*Last checked: 2026-08-06T17:22:10Z by opencode (glm-5.2)*
