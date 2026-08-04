# MintLint

MintLint measures and grades maintainability signals for supported source languages.
The metric thresholds, category aggregation, breadth-gated file roll-up, and rating bands
are documented in [MintLintAlgo.md](MintLintAlgo.md).

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

*Last checked: 2026-08-04T12:05:26Z by opencode (glm-5.2)*
