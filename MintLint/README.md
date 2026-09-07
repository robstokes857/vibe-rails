# MintLint

MintLint measures and grades maintainability signals for supported source languages.
The metric thresholds and weights live in [ScoringProfile](Scoring/ScoringProfile.cs).
[ScoreCalculator](Scoring/ScoreCalculator.cs) implements category aggregation,
breadth-gated file roll-up, and rating bands.

Why those numbers are what they are is the tuning journal, kept in the private
`vibe-books` repository at
[mintlint/MintLintAlgo.md](../../vibe-books/mintlint/MintLintAlgo.md) with sibling
checkouts. Read it before changing grading and record the change there: the
aggregation knobs are tuned against hand-computed fixture values in
`Tests/MintLintTests`, so a change that looks local to `ScoreCalculator` moves
numbers the tests assert by hand.

## VibeRails change-scoped grading

Git Guard and the Code quality changes scan apply a **scope gate before stage 1
(measurement)**:

1. VibeRails asks Git for a zero-context patch for the staged, working-tree, or unpushed
   comparison.
2. Only added (`+`) source lines are passed to `MintLintAnalyzer`.
3. Removed lines are never scored. A removal-only edit therefore produces no MintLint
   file score.
4. New files are entirely added and are analyzed in full.
5. Patch-fragment line numbers are mapped back to the current complete file so findings
   still open on the correct source line.

This changes **which text enters the measurement stage**, not the metric formulas,
normalization thresholds, category weights, breadth-gated roll-up, or rating bands.
The standalone `MintLintAnalyzer.AnalyzePath` and `AnalyzeFile` APIs continue to analyze
complete files; change scoping belongs to VibeRails’ Git preflight integration.

---

*Last checked: 2026-08-06T18:21:17Z by opencode (glm-5.2)*
