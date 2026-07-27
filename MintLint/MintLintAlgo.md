# MintLint grading algorithm

This is the tuning journal and spec for how MintLint turns raw metrics into grades.
The per-metric measuring algorithms are considered settled; **all grading tuning happens
in the aggregation layers described here**. When the grading feels wrong, come back to
this file, pick the right knob, and log what you changed at the bottom.

## The pipeline

Raw numbers flow through five stages. Each stage lives in exactly one place:

| Stage | What happens | Where |
|---|---|---|
| 1. Measure | Raw metrics per function/class/file (cyclomatic, LCOM4, duplication, …) | `MintLintAnalyzer`, `MetricEngine`, `DuplicationAnalyzer`, `TestabilityAnalyzer`, `ImpactAnalyzer` |
| 2. Normalize | Each raw value → 0–100 *concern* via warn/critical thresholds: 0→warn maps to 0–50, warn→critical maps to 50–100, ≥critical pegs at 100 | `MintLintScorer.Normalize`, thresholds in `ScoringProfile.Thresholds` |
| 3. Category | Category concern = **worst metric** in the category (worst-signal-wins) | `MintLintScorer.ScoreCategory` |
| 4. File roll-up | **Breadth-gated**: file concern = the `BreadthRank`-th worst weight-adjusted category, floored at `DepthFloor` × the single worst | `MintLintScorer.RollUp`, knobs in `ScoringProfile` |
| 5. Rating | <30 Clean · <55 Okay · <75 NeedsWork · ≥75 AtRisk; scan overall = mean of file concerns | `MintLintScorer.RatingFor`, `MintLintScorer.Score(files)` |

On top of that, the VibeRails layer (not MintLint itself) computes **priority** =
`EffectiveConcern × (1 + log10(1 + referencedBy))` and discounts pre-existing (baseline)
concern to 50% so legacy debt doesn't outrank fresh damage — see
`VibeRails/Services/GitPreflight/MintLintReportFactory.cs`.

## Why the roll-up is breadth-gated (2026-07-26)

The original stage-4 rule was "worst weighted category wins". Combined with
worst-metric-wins inside each category, a **single** saturated metric set the whole
file's grade. And several metrics saturate on nearly every real file:

- `lack_of_cohesion` (warn 2 / critical 4): any real class with a few fields hits 100.
- `ambient_dependencies` (warn 3 / critical 10): any browser-facing JS (DOM access) or
  process-level C# hits 100.
- `hard_coded_dependencies` (warn 3 / critical 8): any composition-root-ish file hits 100.

Result on a real changeset (2026-07-26 sample, 39 files): 25+ files scored exactly
100/AtRisk, overall risk 90.1. **If everything is bad, nothing is bad** — the report
carried no signal about which files actually deserved attention.

The fix keeps stages 1–3 untouched and changes only stage 4:

```
weighted[i] = category[i].Score × category[i].Weight   (sorted worst-first)
file score  = max( weighted[BreadthRank − 1],  weighted[0] × DepthFloor )
```

- **`BreadthRank` = 4** — a file is only as bad as its 4th-worst dimension, so it takes
  ~4 independent smells to flag a file. One monster function or one incohesive class no
  longer condemns a file on its own. `BreadthRank = 1` restores the old behavior exactly.
- **`DepthFloor` = 0.3** — a single catastrophic dimension is dampened, not erased: a
  lone saturated category still yields 30, the bottom of the Okay band, never Clean.

A useful structural consequence of the weights (Size 0.7, Duplication 0.6): those
categories alone can never push a file past 70/60, so reaching AtRisk (≥75) requires at
least four of the *high-weight* dimensions (Complexity, Cohesion, Testability ×1.0;
Coupling, Maintainability ×0.8) to be severe simultaneously. That is exactly the "3 or
4 things must be wrong" intent.

### Effect on the 2026-07-26 sample

| File | Old | New (4th-worst weighted) |
|---|---|---|
| TerminalRunner.cs (cognitive 104) | 100 AtRisk | 80 AtRisk |
| TerminalSessionService.cs | 100 AtRisk | 80 AtRisk |
| Repository.cs (1908 LOC, 88 methods) | 100 AtRisk | 80 AtRisk |
| EnvironmentRoutes.cs | 100 AtRisk | 80 AtRisk |
| jobs-controller.js / code-analyzer-dashboard.js | 100 AtRisk | 80 AtRisk |
| AnsiParser.cs (state machine — complex by nature, zero coupling) | 100 AtRisk | 70 NeedsWork |
| WaitingForUserInputObserver.cs | 100 AtRisk | 50 Okay |
| TokenSavingsStore.cs | 100 AtRisk | 35 Okay |
| ParserConfigs.cs | 100 AtRisk | 30 Okay (depth floor) |

Roughly 8 of 39 files stay flagged — the genuine multi-dimensional monsters — and the
overall mean drops from ~90 to the mid-50s.

## The knobs, in the order to reach for them

All in `MintLint/Scoring/ScoringProfile.cs` unless noted.

1. **`BreadthRank`** (default 4) — how many bad dimensions it takes. Too many files
   flagged → raise to 5. Genuinely rotten files slipping through → lower to 3.
2. **`DepthFloor`** (default 0.3) — how visible a single catastrophic dimension stays.
   Raise toward 0.5 if "one insane function" files feel under-reported; it only sets a
   floor, so it can never re-inflate multi-dimension scores.
3. **Rating bands** (`MintLintScorer.RatingFor`: 30/55/75) — shift where the labels
   land without changing relative ordering.
4. **`CategoryWeights`** — de-emphasize a whole dimension (e.g. Duplication is already
   0.6 because generated/DTO code trips it).
5. **`Thresholds`** — per-metric warn/critical. *Deliberately untouched in the
   2026-07-26 change.* If grading still feels harsh after roll-up tuning, the known
   over-eager ones are `lack_of_cohesion` (2/4), `ambient_dependencies` (3/10), and
   `hard_coded_dependencies` (3/8). Loosening these changes stage-2 scoring, so treat it
   as a bigger step and re-derive the fixture tests.

Never tune by editing stage-1 measurement code — grades must stay explainable as
"threshold policy", not "the analyzer changed what it counts".

## Test contract

`Tests/MintLintTests/MintLintGradingTests.cs` pins **hand-derived** numbers, not
snapshots. Every pinned overall comes with a comment naming the categories, worst-first,
and which one is the 4th (or that the depth floor carried). If you touch any knob:

- Re-derive the messy-fixture overalls (currently 49.1 / 50.0 / 48.0 / 43.5 Okay
  single-file; 57.1 NeedsWork for cs/ts/js in a joint scan where cross-file
  Duplication saturates).
- `Scorer_DampensALoneHotCategory_ToTheDepthFloor` encodes knob 2, and
  `Scorer_RequiresFourHotCategories_ForAtRisk` encodes knob 1 — update them to match
  the new policy, don't delete them.
- `MintLintSourceInputTests.ScanSources_AppliesProfileAndOrdersEqualScoresByPath`
  pins the 30.0 depth-floor value.

## Change log

- **2026-07-26** — Grading was flagging everything: 25+ of 39 changed files at exactly
  100/AtRisk (overall 90.1). Replaced worst-category-wins roll-up with breadth gating
  (`BreadthRank` 4, `DepthFloor` 0.3). Stages 1–3, thresholds, weights, and rating
  bands unchanged. Expected follow-ups if still mis-calibrated: tweak `BreadthRank`/
  `DepthFloor` first, thresholds for LCOM4/ambient/hard-coded deps only as a later,
  deliberate step.
