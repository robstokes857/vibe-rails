# TokenSaver paper change log

## 0.3 — October 5, 2026

Reconciled Codex verification of Claude’s R-01–R-20. See `review-notes-codex.md` for evidence, disagreements and exact replacement decisions.

- Added `tokensaver-research-v0.3.md` and `tokensaver-v0.2-to-v0.3.diff`. The original `tokensaver-research-review.md`, Word/PDF/LaTeX exports, ZIP, evidence and baseline hashes remain version 0.2.
- Distinguished mechanical per-request byte reduction from a complete-task counterfactual; documented current meter filters and differing archive selection.
- Corrected Study A mixed units and removed their interpretation as measured replay counts. Versioned evidence preserves the historical label conflict and missing scratch computation.
- Qualified classifier frequencies, conversation keys, possible historical-build contamination and T0 note provenance without changing recorded tiers.
- Restored historical cost-model results and recommendations as sensitivity context; rejected unsupported causal loss, guaranteed cache misses and power-impossibility conclusions.
- Narrowed test/replay claims, recounted saved flags and documented 45 changed after-texts. Added classifier mixture and the available incomplete Codex cache usage totals.
- Clarified detector-positive rates, pipeline stages and post-cleanup control-character behavior; preserved task-quality uncertainty.
- Clarified theorem quantifiers, model units and signed costs; made the experiment’s arms, estimand, blinding, cache conditions, power and billing requirements explicit.
- Rebuilt Figures 1 (separate units), 4 (mitigation commit date and qualified annotation) and 6 (recovery cost H notation) in `figures/v0.3/` as PNG/SVG from packaged aggregates. Other numeric figures remain valid with revised manuscript captions.
- Added snapshot-specific feedback for the concurrently authored HTML technical note. Following automated review dd8e312a19ad4ba5980ad96ed9dfc37b, applied the supported corrections in technical note 1.1: actual-token versus meter units, conditional experiment sizing, monetary versus probability units, narrower test/causal/cache claims, and pricing/counting provenance. Regenerated both note SVGs and its PDF; visually checked all 11 PDF pages. `technical-note-review.diff` records the source changes from the inspected 1.0 snapshot and passes reverse-application checking. This note is separate from the 0.3 manuscript.

No live data collection, database queries, new pipeline execution, model inference, publication or product behavior changes. No new Word/PDF/LaTeX export is represented as 0.3. Validation results are in `review-validation-v0.3.json`.

## 0.2 — October 5, 2026

Original package at commit `9929c053`: Markdown, Word, PDF and LaTeX, nine figures, saved aggregate evidence, provenance and review instructions. Includes the bounded September audit, historical replay, provider usage and file-read study. Baseline identity remains in `review-baseline.json`.
