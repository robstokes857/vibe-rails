# TokenSaver research review brief

Review the version 0.2 manuscript for Robert Stokes. Try to falsify the arguments, find unsupported inferences, and propose the smallest defensible corrections. Preserve the distinction between measured request reduction, historical replay results, mathematical propositions, and unmeasured task savings.

## Files and revision workflow

Use `tokensaver-research-review.md` for agent edits and diffs, or `output/docx/tokensaver-research-editable.docx` for Word comments and tracked changes. The Word document has editable paragraphs, tables, and six native equations; its nine charts are embedded images, with separate figures and aggregate data supplied. Track Changes is enabled for future Word edits. Both files represent the same version 0.2 research claims.

Keep your own review copy. Return the edited manuscript plus `review-notes-<reviewer>.md`, or a unified diff against the original Markdown. Identify the baseline as version 0.2; file hashes are in `review-baseline.json`. Do not run the builders over a revised manuscript: they regenerate exports from the older source blocks and would overwrite edits. Bring the revised file and notes back to this chat for reconciliation into the next paper version. Review output is a proposed change until reconciled.

## Evidence provided

- `evidence.json`: published aggregates and derived quantities with units.
- `daily-series.csv`: the rounded daily series used by the paper.
- `source-manifest.json`: local source paths, revisions and SHA-256 hashes.
- `validation.json`: the existing focused 475-test record and calculation checks.
- `research-discovery.txt`: wider search coverage and the treatment of older studies.
- `figures/`: all nine figures in PNG, SVG and PDF.
- `tokensaver-research-draft.tex`: original editable mathematical source.

Private research files named by the manifest are not all included. A hash establishes file identity, not that the reviewer has independently inspected it. When the needed source is unavailable, identify the exact missing file or field and mark the finding unresolved. Do not treat quoted summaries as independent replication.

## Stable claim identifiers

Use these IDs in comments and revision notes. Retain an ID for the same claim across revisions even if section numbers or wording change.

| ID | Location and claim | Review question |
| --- | --- | --- |
| TS-C01 | Sections 4 and 8; measured request-body reduction | Do units, numerators, denominators, dates and request filters support every rate? Are overlapping populations kept separate? |
| TS-C02 | Section 2; byte non-growth proposition | Do the assumptions and implementation establish the splice inequality? Can parsing, escaping, overlapping spans or adapter behavior invalidate its application? |
| TS-C03 | Section 3; distinct content, replay and caching | Do definitions support the 12.60x and 39.81x ratios? Are characters, actual tokens and monetary costs kept distinct? |
| TS-C04 | Section 5; related calls after truncation | Are tiers, exclusions and unresolved-case bounds correct? Does wording imply causal recovery, extra turns or recovered content without evidence? |
| TS-C05 | Section 6; net-cost and break-even model | Are assumptions explicit and dimensions correct? Could overlapping costs, an inappropriate baseline or unstated dependencies change the result? |
| TS-C06 | Section 7; task-answer preservation theorem | Is the compression-fiber condition necessary and sufficient as stated? Does the counterexample establish precisely the claimed limitation? |
| TS-C07 | Section 7; focused tests | Does the 475-test record support the stated scope? Are sample coverage, universal guarantees and semantic safety distinguished? |
| TS-C08 | Section 8; historical C# replay | Do 8,177 variants and 32,708 cases support the invariant results? Are after-text inputs, UTF-16 units, canonical JSON and adapter exclusions stated correctly? |
| TS-C09 | Section 9; file-read mitigation and stage interactions | Do 205/261 and 217/288 support the coverage claims? Are historical removal and candidate opportunities distinguished from realized savings? |
| TS-C10 | Sections 10 and 11; actual usage and capture coverage | Are categories disjoint and complete where claimed? Are missing usage, capture limits and the absent uncompressed counterfactual acknowledged? |

Also examine the proposed experiment in section 12: pairing, cache control, task-level uncertainty, noninferiority, stopping rules and treatment of failures. An experiment proposal is not an observed result.

## Severity and evidence

- **Major:** changes a central result, invalidates a proof or creates an unsupported causal conclusion.
- **Moderate:** materially affects units, population, provenance, reproducibility or interpretation of supporting evidence.
- **Minor:** improves precision, citation placement or presentation without changing the conclusion.

For every finding, return:

```text
Finding: R-01
Reviewer: Name or agent identifier
Baseline: TokenSaver paper 0.2
Claim ID: TS-C__
Severity: Major / Moderate / Minor
Assessment: Contradicted / Needs qualification / Unresolved
Location: Section, paragraph or figure; quote the relevant sentence
Problem: Concrete error or unsupported inference
Evidence: Supplied file and line/field, calculation, or counterexample
Proposed revision: Exact replacement wording or equation
Effect: Which result, figure or conclusion must change
Remaining uncertainty: What the supplied evidence cannot resolve
```

List the claim IDs examined without a finding and the sources actually inspected. Absence of a finding is not independent replication. Separate factual corrections from stylistic preferences. Report conflicting evidence rather than silently selecting the most favorable result.

Use source inspection and arithmetic checks for this review. New experiments, paid inference, application database queries and fresh traffic collection require a separate task. If resolving a claim requires them, state the missing observation and the qualification needed now. Never fabricate support or turn a modeled estimate into a measured result.
