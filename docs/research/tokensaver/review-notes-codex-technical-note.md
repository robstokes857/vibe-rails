# Follow-up review of the concurrent technical note

Codex, October 5, 2026, VIBE-63. This supplements `review-notes-codex.md` after `note/tokens-saved-technical-note.html` appeared during the shared-checkout review. Original inspected HTML snapshot SHA-256: `b8a1a0d6e63206226330ccaaedd4f91d8544450e13960f3da13c61aea44f78a4`. The author was editing concurrently; line numbers and quotations below identify that snapshot. Supported corrections have since been applied as technical note 1.1, as recorded in the resolution below. The reconciled 0.3 manuscript is a separate deliverable.

The inspected 1.0 note needed corrections to Proposition 2, its consequence and the power-impossibility claim. Automated review dd8e312a19ad4ba5980ad96ed9dfc37b independently confirmed those blockers (F1/F2) and two supporting qualifications (N1/N2). `technical-note-review.diff` now records the applied 1.1 source changes from that snapshot, including the chart generator and PDF footer.

## Resolution in technical note 1.1

- **TN-01 / F1:** equation (4), its proof, Figure 2, Table 4 and notation now describe actual token appearances. The consequence states the missing `(4d/b)` meter calibration and full-request token-delta requirement.
- **TN-02 / F2:** replaced the impossibility claim with pilot-based sizing and explicitly hypothetical variance/effect assumptions.
- **TN-03–TN-06 / N2:** separated observed usage from avoided billing, corrected meter filters and repeatability/cache limits, narrowed test coverage and historical causal claims, and qualified public-counter cadence and source provenance.
- **TN-07 / N1:** identified probabilities as dimensionless, required a monetary valuation of quality loss and nonoverlapping costs, stated the theorem’s nonempty domain, and removed guaranteed-recovery implications.
- Qualified illustrative historical pricing and counting methods: the original author’s external access claim was not independently verified. Exploratory before/after comparisons do not establish causation.
- Regenerated both SVG charts and the 11-page PDF. All PDF pages were visually inspected; equations, tables, charts, captions and footers are legible without clipping. The applied source diff passes `git apply --reverse --check`. Current artifact hashes and separate reviewer test results are in `review-validation-v0.3.json`.

The findings below preserve the original review evidence and proposed wording; they are not assertions that those defects remain in 1.1. No publication, product change or matched task experiment occurred.

## TN-01 — The meter does not count d actual tokens per appearance

Severity: **Major**. Claims: TS-C03, TS-C05. HTML section 6.2, lines 329–336, Figure 2, Table 4 and the consequence paragraph.

The proof says “the meter counts d·m tokens” after defining d as actual tokens. Source `ITokenSavingsStore.cs:20–26` instead computes `floor(total removed bytes / 4)`. For a block removing b encoded bytes per appearance and d actual tokens per appearance, the meter adds approximately bm/4 estimated tokens (aggregate integer rounding aside). The avoided cost per estimated token is approximately `(4d/b) * [w + α(m−1)]/m`. Without measured b/d the displayed curves do not establish the money value of a meter unit. Counterexample: d=1,000, b=2,000 and m=10 yield 5,000 estimated meter tokens, not 10,000 actual appearances. The missing factor is 2.

Replacement: “Dividing G by d·m gives the avoided cost per **actual token appearance**, v(m) = [w + α(m−1)]/m. This is not cost per byte/4 meter estimate; that conversion additionally requires the measured ratio of token reduction to byte reduction.” Relabel the chart, alt text, caption, table and notation consistently, or explicitly include an assumed b=4d calibration. The derivative and limit are correct for the stated hypothetical actual-token quantity. Keep cache hits, boundaries, unchanged continuation and token additivity as assumptions; d must mean the full-request token delta attributable to the block, not merely a tokenizer count of the isolated substring.

## TN-02 — Small-effect detection is not impossible

Severity: **Major**. Claim: experiment. Section 10, line 417.

“Effects as small as 0.2% are beyond their reach … so such trials can bound cost increases rather than establish savings” treats assumed variance and a gross byte rate as facts about task-cost power. The counterexamples and sample-size calculation are in main review R-04. A noninferiority bound also needs its own margin and power.

Replacement: “A pilot must estimate paired task-cost variance and quality disagreement rates before sizing superiority or noninferiority trials. Under an illustrative paired log-cost SD of 0.3, detecting a 0.2% log-cost effect would require about 176,000 independent pairs at two-sided 5% significance and 80% power. Neither that SD nor a 0.2% task-cost effect follows from the byte meter.”

## TN-03 — Actual usage was observed; the savings counterfactual was not

Severity: **Moderate**. Claim: TS-C10. “At a glance,” line 180; section 6.1 and Appendix C.

The “Not measured” item lists “Billed tokens” without distinguishing observed provider usage from avoided usage. The same note quotes 558,868,027 actual input tokens and cache categories. The monetary/billing agreement is unknown, but usage buckets were measured.

Replacement: “Not established: avoided billable tokens or money relative to an uncompressed task, total task-cost effects, or preserved task quality. Provider-reported usage was recorded for the observed requests.” The 3.04 ratio uses **submitted** bytes divided by **forwarded-request** usage; identify those sides rather than calling it a matched tokenizer calibration. It is not the removed-text b/d needed by TN-01. Also do not replace “estimated input tokens” with an unqualified actual-token claim in section 1.2.

## TN-04 — Repeatability, controls and tests need the qualifications already found

Severity: **Moderate**. Claims: TS-C02, TS-C07, TS-C09. Sections 1.1–1.2, 2 and 4.

- Add JSON Content-Type and detectable-body eligibility to the meter list (`*BodyTransform.AppliesTo`).
- “Same removed bytes” requires the same input, metadata, plan and encoding. Pausing/settings changes **can**, rather than necessarily do, change historical bytes. Two cache-transition opportunities do not guarantee two misses (main R-07).
- The latest snapshot already distinguishes Windows line endings; control-character checks more generally apply **after cleanup**, so CRLF is normalized by default before lossy stages. Literal characters and byte/KB units must not be interchanged: the 4,096 and 262,144 thresholds use UTF-16 units, with the latter checked after dedupe. The 30,000-character Bash cap is a historical harness observation, not a universal 30 KB byte cap.
- Codex tests include whole-body no-op equality and byte-shrinkage assertions (`CodexResponsesRewriterTests.cs:35,56–58,138–140,212`). “Parsed values rather than whole bodies” is too broad. State the narrower changed-body/escaping coverage instead.
- Attribute the 64 cache misses to Claude Window B; saved after-text changes have possible causes, not resolved causal attribution.

The revised 0.3 manuscript contains exact replacement wording for these points. No new product tests were run.

## TN-05 — Historical regimes do not quantify the cause of removal

Severity: **Moderate**. Claims: TS-C01, TS-C09. Figure 1 alt text, Table 3, section 5 bullets.

The weighted period rates 1.02/0.20%, 14.93/1.11%, 5.64/0.20% and 3.39/0.19% reproduce from the CSV. But the statement “That [file-read truncation] is the source of most of Codex’s 15%” lacks a matched per-stage/per-command byte attribution for that period. Shares of lifetime removal before a date cannot establish its mechanism. “Fixed 30 Aug” should identify the mitigation commit and unknown deployment timing. The alt-text range 15–28% for August 18–29 omits 5.03% and 9.91% days. Use “variable daily rates, reaching 28.21%.”

The archive recount uses HTTP-200 model requests, not literally every relayed request. State its exclusions and that its population differs by construction; “slightly” is not quantified. Classifier exclusion yields ≥0.295% only under zero removal in that family and the system-character byte lower bound; “about 0.3%” is not an exact recalculated population rate. These are main review R-06/R-08/R-09 qualifications.

## TN-06 — Public-counter storage is supported; reporting cadence needs limits

Severity: **Minor**. Claim: public-counter description. Section 1.3.

Inspected sibling frontend `Controllers/TokenSavingsController.cs` and `PublicTokenSavingsController.cs` substantiate the per-key-hash/computer-name maximum, summed rows and 15-minute **process-local** cache. Rekeying/renaming can count the same lifetime again. This is source verification, not an audit of deployed data or guarantees against administrative changes/cache differences across server processes.

`VibeRails/MapRegisterServices.cs:368–392` registers publishing only for active root backends. `TokenSavingsPublishJob.cs:37–49,158–189` makes publishing enabled by default with a configurable default 15-minute interval; failures/locks can skip ticks. Replace “Each installation with an API key reports … every 15 minutes” with “While an active root backend is open, enabled publishing attempts to report the lifetime estimate using a configurable interval, 15 minutes by default, when a usable API key and endpoint are available.” There is no background daemon.

## TN-07 — Quality loss needs a defined valuation and event unit

Severity: **Moderate**. Claim: TS-C05. Section 7.2.

The equation can be a valid **utility** model if F is explicitly valued in the same monetary units and recovery/quality/cache terms do not overlap. No F is estimated here. The sentence “All are in base-price units” is also dimensionally false for q and r: they are probabilities. Multiple recoveries and silent failure followed by later recovery need a defined horizon and event partition.

Replacement: “For one prespecified intervention and continuation, q and r are dimensionless event probabilities; G, L, F and K are in a common monetary unit. F requires an explicit valuation of quality loss, and the terms must be defined without overlap. Without that valuation, report monetary net cost and task quality separately.” The 0.3 manuscript follows the latter approach. The static preservation theorem is sound under the usual nonempty task domain; interactive recovery can incur cost, but does not guarantee successful recovery for every task.

## Checks and remaining limits

Inspected the full technical-note prose, formulas, table data, chart generator and render script. Period arithmetic and conditional value-table arithmetic agree with the saved aggregates and formula. The source proof of byte non-growth and fiber-preservation argument remain valid under their assumptions. Initial inspection was text-only; the corrected 1.1 HTML was subsequently rendered and all 11 PDF pages visually checked. Do not infer current provider-specific pricing from generic curves. Original snapshot, current artifact and frontend controller hashes are recorded in `review-validation-v0.3.json`; later author changes require checking the affected findings again.
