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

The bare form remains supported for existing and hand-authored files. `AgentFileService` writes an
explicit `(WARN)` for new default-level rules so an argument ending in `(STOP)`, `(WARN)`,
`(COMMIT)`, `(SKIP)`, or `(DISABLED)` cannot be mistaken for the rule's enforcement suffix.

Enforcement tokens are case-insensitive: `WARN`, `COMMIT`, `STOP`, `SKIP`, `DISABLED`.

### 5. Scope

A rule applies to changed files under the directory of the `vc.rules.md` that declares it. A nested
`vc.rules.md` governs its own subtree and nothing above it (`GetScopedFiles`). Rules with no changed
files in scope are not evaluated and do not count toward "applicable rule(s)".

Duplicate rules from the same source file are collapsed (`RulesTool.AddRuleIfNew`).

As of 2026-09-07, file-change documentation rules (`Log all file changes` and `Log file changes
> N lines`) treat their declaring vc.rules.md as implicit documentation. Creating or editing that
file does not require adding a Files entry for itself. This exemption applies only to that exact
declaring file: a parent policy still requires documentation for nested vc.rules.md files, and
other changed files remain subject to the rule. `RuleFileDocumentation` shares the identity check
between Git Guard, the Rules page, and legacy validators; staged-hook regression tests cover new
and existing policy files.

### 6. Bytes

A leading UTF-8 byte-order mark is ignored (2026-09-26). The hook decodes index bytes
without a BOM-stripping reader, so a rules heading on the first line of a file saved with a
BOM was invisible to the hook while the Rules page and the MCP `validate_vca` tool, whose
readers strip it, both showed the rule. CRLF files are read line by line as before.

### 7. Parameterized path locks

`File Lock('path/to/file')` protects one exact Git path. `Directory Lock('path/to/directory')`
protects the directory and every descendant. Paths are relative to the directory containing the
declaring vc.rules.md; absolute paths and `..` escapes are invalid. Additions, modifications,
deletions, and both sides of renames count as changes. Matching uses Git path boundaries, so a lock
on `src/locked` does not match `src/locked-old`.

The declaring vc.rules.md is excluded from its own lock. This is the control-plane escape hatch that
keeps a STOP rule editable and removable; other vc.rules.md files beneath a directory lock remain
ordinary protected content.

## Thresholds

`Code quality minimum A`, `Code quality minimum B`, and `Code quality minimum C` gate the
**overall** MintLint grade of added code in supported source files under the declaring rule file.
They never require each file to pass separately. A requires health ≥90, B ≥80, and C ≥70;
C is the lowest configurable minimum. D/F are still possible results, but are not thresholds.
Health is 100 minus the existing scan's overall concern score. Hooks and MCP use the staged
snapshot; Rules-page validation uses working-tree additions. Deletions, unchanged renames,
binary files and unsupported file types contribute no added code. An empty scan is explicitly
skipped. Missing content or an invalid score must not silently pass. Report ignore preferences
are presentation settings and do not exempt code from this repository rule.

Quality failures follow the standard WARN/COMMIT/STOP policy, including re-evaluation at
commit-msg. Invalid handwritten thresholds warn as unsupported; rule writers accept only A/B/C.
Rules-page and legacy validation report snapshot I/O, access, invalid-operation and Git timeout
failures as `UNSUPPORTED:` quality results at the declared level, then continue unrelated rules.
Cancellation still propagates to the caller.
`CodeQualityRule` owns grading and scope, shared by the active validators and legacy adapter.
Regression fixtures and real-hook coverage live under `Tests/VcaRegression/Fixtures/code-quality`
and `CodeQualityTests.cs`; score boundaries and aggregate behavior are in `CodeQualityRuleTests.cs`.

Pinned by `Tests/VcaRegression` on 2026-09-26, after the regression sweep found the first one
wrong in all three validators:

- `Cyclomatic complexity < N` rejects an estimate of exactly N. It reads "under N"; 20 is not
  under 20. The estimate is 1 + decision keywords in the staged content of code files.
- `Log file changes > N lines` admits exactly N changed lines (numstat added + deleted of the
  staged delta) and requires a Files entry from N+1. Binary files cannot be counted and report
  `UNSUPPORTED:` at the declared level.
- Coverage rules decide "is production code staged" with `FileClassifier.IsTestFile`, the one
  classifier shared by the hook, the Rules page and the legacy validators. A test is named by a
  convention with a boundary: a capitalized suffix (`MyTest.cs`, `ABTest.cs`), a separated
  suffix (`parser_test.go`, `user_spec.rb`), a dotted segment (`calc.spec.ts`), a prefix
  (`test_math.py`, `TestOrders.java`), a whole stem (`test.py`), or a test directory. A
  substring match on "test"/"spec" used to treat `src/Inspector.cs` and `src/LatestReport.cs`
  as tests, and a case-blind "ends with test" treated `Contest.cs` as one; both let the gate
  pass silently.

## Enforcement levels

`Require VibeRails session link` requires at least one public session replay URL in the final
commit message. It defers at pre-commit and in Rules-page validation, then follows the normal
WARN/COMMIT/STOP policy at commit-msg (including message-only commits). `SessionShareCommitRule`
checks dedicated `vibe-share:<url>` lines first, then scans URLs in prose or Markdown. It accepts
the sharing service's HTTPS `viberails.ai/shared/session?key=<64 lowercase hex characters>` shape;
API endpoints, unrelated hosts and malformed keys do not count. Git comment lines are stripped
before validation. Multiple links are allowed; one valid link satisfies the rule.

Agents can call `create_session_share_link(displayName: "Description of the work")`, then copy its
returned `vibe-share:` line into the commit message, one line per session. The rule only checks
text: it makes no network request and cannot prove the session belongs to the commit, has uploaded,
or remains accessible. Active-session links are accepted; sharing links expire and can be revoked.
The Rules catalog and deferred/violation messages carry this guidance. The rule is selectable,
not automatically enabled in every repository.

As of 2026-09-07, `Check commit message for: wip, temporary, do not merge` uses
`CommitMessageWordRule` for syntax and matching across Git Guard and the legacy validator.
It accepts a plain comma-separated list of whole words or literal phrases, trims entries,
deduplicates ignoring case, and matches case-insensitively with non-word characters (or the
message ends) around the literal entry. This preserves whole-word matching and also lets terms
such as `TODO:`, `C++`, and `[skip]` match naturally. Empty entries, quote wrappers, and control
characters are rejected by all rule writers.
The bare catalog template remains selectable but is not a complete rule. Existing malformed
lists produce an explicit unsupported warning, even at STOP; valid lists defer until commit-msg.
Rules-page validation reports the same configuration error or a clear deferred result.

| Level | Pre-commit | Commit-msg | Meaning |
| --- | --- | --- | --- |
| `WARN` | reported, allows commit | — | Visible, never blocking. |
| `COMMIT` | reported, allows commit | **blocks** until acknowledged | Needs `[VCA:<source>:<slug>] Reason: …` in the commit message. |
| `STOP` | **blocks** | blocks | Cannot be acknowledged or bypassed. |
| `SKIP` / `DISABLED` | not evaluated | not evaluated | An explicit opt-out. |

`COMMIT` is deliberately non-blocking at pre-commit: the acknowledgment lives in a commit message
that does not exist yet. The commit-msg hook is where it is enforced.

## Signed commits: `Human verified` and `Signed Approval` (2026-10-20)

Two rules a person satisfies by signing the commit with a Settings > KEYS signing key
(`HumanSignatureRule`, `CommitSignatureFormat`):

- `Human verified` needs, for every commit in scope, a signature from a key registered on
  viberails.ai to an account with a verified email. It is also a commit-message rule, so a
  message-only commit (`--amend` with nothing staged) needs one too.
- `Signed Approval('db.cs', 'owner@example.com, other@example.com')` applies only when the path
  changes. The path is a file or directory relative to the declaring vc.rules.md and may not escape
  it (the path-lock resolver, with "approval path" wording). The signer's verified email must be
  listed, compared without case. Text that does not parse is Unrecognized (warns, never blocks), for
  the path-lock reason. Unlike locks, the declaring vc.rules.md is not exempt, so `'.'` also covers
  edits to the policy file.

**What is signed.** Not the commit id: it hashes the message, and the message carries the
signature. The signature (RSA-PSS-SHA256) covers `VibeRails human-verified commit v1\ntree <id>\n`,
where the id is the staged tree from the snapshot (`StagedIdentity.StagedTree`, i.e. `git
write-tree`). That is exactly the content being committed, so any staged change after signing
invalidates it, while amending only the message keeps it valid.

**Where it lives.** One trailer per signature, after Git comments are stripped, at most five read:
`vibe-signature: v1 key=<viberails.ai key id> fp=<fingerprint> tree=<tree> sig=<base64>`.

**Verification.** Deferred at pre-commit, on the Rules page and in MCP `validate_vca`. At commit-msg,
a signature over another tree fails locally ("the staged content changed after signing"); the rest
go to `POST https://viberails.ai/public/api/v1/signatures/verify` (`ViberailsCommitSignatureVerifier`),
which checks the server's registered key and returns the account email only for a valid signature
with a verified email. It fails closed: offline, rate-limited or a server error is a violation at the
declared level, never a pass. Definitive answers are cached for the process; failures are not.

**The popup.** When commit-msg is blocked only by signature findings (COMMIT or STOP) and a person
can answer (the hook script's `--prompt-acknowledgment`), `VcaHookRunner` offers the signing UI up
to three times, re-running the preflight after each signature. `HumanSignaturePromptLauncher` runs
it inside the Git Guard popup console when the hook is already there; otherwise it opens one with
`--vca-hook sign-commit-msg` through `VcaHookConsoleRespawn` (CREATE_NEW_CONSOLE). It never prompts
in the terminal that ran `git commit`, which may be an agent's pseudo-terminal. No popup opens in
CI, without a desktop or off Windows; the commit is blocked with the rule's guidance. It is not
offered while an unrelated STOP violation, or an unreachable viberails.ai, would block anyway.
`HumanSignaturePopup` shows the repository, subject, tree and changed files, lists keys synced for
the current API key, reads the password key by key with echo off (redirected input refused,
10-minute idle timeout, three attempts), signs with `SigningKeyService.SignCommitTreeAsync`,
confirms with viberails.ai and appends the trailer above any scissors line.

**Policy from HEAD.** Rules normally come from the staged vc.rules.md. For these two rules only,
`RulesTool.AddCommittedSignatureRules` also reads the committed text of every modified, renamed or
deleted vc.rules.md: a rule the commit removes or weakens is enforced at its committed level, and
removing an approval counts as touching its path. Taking a gate away therefore needs the signature
the gate asks for. The snapshot provider keeps `PreviousContent` for a deleted vc.rules.md for this.

**Threat model.** It stops an agent from producing an approval: the key password is never stored,
the dashboard's Sign message route refuses the commit-approval prefix, no MCP tool signs, and the
popup is a separate window. It does not stop `git commit --no-verify` (which skips every hook), a
process running as the user that knows the password (it can decrypt the key file itself), or UI
automation of the popup window. A signature proves the key holder approved the tree, not who was at
the keyboard. Use STOP: at COMMIT an acknowledgment token replaces the signature.

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
| `HumanSignatureRule.cs` | Human verified / Signed Approval syntax, the `vibe-signature:` trailer and the signed payload. |
| `CommitSignatureVerifier.cs` | Commit-signature verification through viberails.ai's public verify endpoint. |
| `Hooks/HumanSignaturePopup.cs`, `Hooks/HumanSignaturePrompt.cs` | The signing popup and the launcher that decides where it may run. |
| `../GitPreflight/` | Step pipeline (VCA → MintLint → automated workflows) and its event stream. |

## Testing

Rules-page file operations use `RuleFilePath` through `AgentFileService.ResolvePathAsync`:
only absolute `vc.rules.md` paths within the current repository are accepted. Check before
existence probes or metadata writes as well as before content I/O. Reject linked components
and Git metadata, and preserve CreateNew semantics when creating a file. Discovery omits
linked files/directories. `RuleFilePathSecurityTests` and `AgentRoutesSecurityTests` pin this
boundary, including rejection with valid API credentials.

| Area | Tests |
| --- | --- |
| Discovery contract | `Tests/Services/VCA/AgentRuleSectionReaderTests.cs` |
| Validation + parsing | `Tests/Services/Mcp/RulesToolTests.cs` |
| Real hooks, real repos | `Tests/Services/VCA/VcaHookEndToEndTests.cs` |
| Every catalog rule × every level, real repos | `Tests/VcaRegression/` (fixture scenarios under `Fixtures/`; see its README) |
| Rules-page CRUD | `Tests/AgentFileServiceTests.cs` |
| Signed commits | `Tests/VcaRegression/HumanSignatureRuleTests.cs` (real hook, fake verifier and popup), `Tests/Services/VCA/HumanSignature*Tests.cs`, `CommitSignatureVerifierTests.cs` |

`VcaHookEndToEndTests` builds throwaway Git repositories and runs the real hook host against them.
Prefer adding cases there for anything that touches what blocks a commit — it is the only layer
that exercises staging, scoping, and exit codes together.

As of 2026-09-07, these tests use the host's internal `RunCoreAsync` service override to point the
real `JobStore` at a per-test database under the throwaway repository's `.git` directory. Keep this
isolation: using the default live user database caused SQLite lock contention with a running app
and made the popup's 20-second completion test time out. Production `RunAsync` retains its normal
service registrations.

## Commit-message trailer cleanup

Git Guard always removes co-author attribution and `Claude-Session:` trailers during `commit-msg`,
before chained hooks and VCA validation read the message. Cleanup is independent of settings;
the legacy `RemoveCoAuthorTrailers` field stays stored for older versions but has no effect here.
Matching is case-insensitive and accepts `Co-authored-by`, `Coauthored-by`, `Co-authored by`,
`Coauthored by`, `Co-author`, and `Coauthor`, regardless of the agent name or email. Only the
terminal trailer block is cleaned; body prose, comments, unrelated trailers, scissors/diff content,
and the existing byte-preservation protections remain in place.

## Hook removal

Removing Git Guard strips only the VibeRails-managed sections from `pre-commit`, `commit-msg`, and
`post-commit`; third-party hook content and preserved sidecars are restored. A repository-local
`.git/.viberails-git-guard-disabled` marker prevents startup auto-install from putting the hooks
back. An explicit Install or Repair action removes that marker and re-enables startup installation.

---

*Last checked: 2026-08-06T17:22:10Z by opencode (glm-5.2)*
