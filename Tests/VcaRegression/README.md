# VCA regression suite

End-to-end regression coverage for every rule in `RulesService.Rule`, run through the real
validation path rather than a rule function in isolation. Each test builds a throwaway Git
repository from a fixture scenario, then runs the same host `vb --vca-hook` runs
(`VcaHookProcessHost.RunCoreAsync`) for pre-commit and commit-msg, and cross-checks the MCP
`validate_vca` reader against it.

This directory is deliberately separate from `Tests/Services/VCA`, which covers the hook host,
presenter, and individual helpers. Put "does rule X do the right thing against a real staged
change" here; put "does the console popup close" there.

## Layout

```
Tests/VcaRegression/
  Fixtures/<family>/<scenario>/     sample repositories (byte-exact, see .gitattributes)
    base/                           committed as the baseline (optional)
    scenario.json                   {"delete": [...], "rename": [{"from","to"}]} (optional)
    staged/                         written and `git add`ed on top (optional)
    unstaged/                       written last and left unstaged: noise the hook must ignore
  VcaRegressionRepository.cs        materializes a scenario and runs the real hooks
  Expect.cs                         transcript assertions + the WARN/COMMIT/STOP contract
  <Rule>Tests.cs                    one class per rule family
  RuleFileParsingTests.cs           heading, section end, fences, forms, scope, CRLF, BOM
  RuleCatalogCoverageTests.cs       every catalog rule has a fixture; fixtures are still byte-exact
```

Rules files are stored as `vc.rules.fixture.md` and materialized as `vc.rules.md`. Under their
real name they would be live policy for this repository: Git Guard reads every `vc.rules.md` in
the index, scoped to its directory, so the STOP fixtures blocked the very commit that added them
and the Rules page would have listed all of them. `RuleCatalogCoverageTests` fails if a real
`vc.rules.md` ever appears under `Fixtures/`.

Any fixture rules file may contain the token `{{LEVEL}}`. The harness replaces it with the
requested enforcement level, so one violating fixture serves the WARN, COMMIT and STOP checks.
Nothing else is rewritten: every other byte reaches the index exactly as it sits on disk, which
is why `Tests/VcaRegression/Fixtures/**` is `-text` in `.gitattributes` and why the temp
repositories force `core.autocrlf=false`.

## The enforcement contract every rule is held to

| Level | pre-commit | commit-msg |
| --- | --- | --- |
| WARN | `[WARN] rule` reported, exit 0 | exit 0, "No VCA commit acknowledgments were required" |
| COMMIT | `[COMMIT] rule` + `[VCA:source:slug]` token, exit 0 | exit 1 until the message carries `token Reason: <non-empty>`; token alone, blank reason, or a `#`-commented line still exit 1 |
| STOP | `[STOP] rule`, `[block] Commit blocked`, exit 1 | exit 1, "blocking validation still fails" |

`EnforcementLevels.AssertPreCommitViolationHonorsLevelAsync` drives all of this for one scenario;
the commit-message rule uses `AssertCommitMessageViolationHonorsLevelAsync` because its violation
can only surface once a message exists.

## Adding a scenario

1. Create `Fixtures/<family>/<scenario>/` with `base/`, `staged/`, `unstaged/` and/or
   `scenario.json` as needed. Keep files small and LF-terminated unless the point of the scenario
   is the bytes themselves.
2. Reference it from a test with `VcaRegressionRepository.CreateAsync("<family>/<scenario>")`,
   optionally passing the enforcement level.
3. Prefer `RunPreCommitAndCrossCheckAsync()` over `RunPreCommitAsync()`; it also asserts that the
   hook and the MCP tool agree on block/acknowledge.
4. Do not add a `.cs` fixture without checking `Tests.csproj` still removes
   `VcaRegression\Fixtures\**` from compilation.

## Running

```
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~Tests.VcaRegression"
```

Every test owns its own temp repository and a per-repository automation store under `.git`, so the
suite never opens the live `~/.vibe_rails/state.db` and is safe to run beside a running dashboard.
