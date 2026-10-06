# Codex verification of Claude’s TokenSaver review

Reviewer: Codex, October 5, 2026. Card: VIBE-63 / VB-NY2MM-135.
Baseline: paper 0.2, Markdown SHA-256 `7fbbd8917a5f3ae3da345e5e492c340dd14e6110a9d7b635e81ff1f2f86a76df`.
Review checked: `review-notes-claude-opus.md`, all R-01–R-20, including its “checks that passed.”

Claude’s central corrections are useful, particularly the Study A unit mismatch, the mechanical rather than counterfactual interpretation of byte savings, and the limits of the tests and replay. Several proposed replacements go beyond their evidence. I reconciled supported changes into `tokensaver-research-v0.3.md`, with a unified diff, versioned evidence and three revised figures. The original manuscript, exports, evidence, figures, review notes and ZIP remain 0.2. Publication and product defaults are unchanged.

This is source inspection and recalculation of saved evidence, not independent replication of collection, a new pipeline replay or a task experiment. No application database was opened. References below use the inspected files’ line numbers; `books/` means the sibling `C:/source/vibe-books/` checkout. All 18 original source-manifest hashes matched. Stable claim IDs TS-C01–TS-C10 were all examined.

## Finding dispositions

Severity describes the scientific consequence of applying the correction incorrectly, using REVIEW.md’s Major/Moderate/Minor scheme. “Accept in part” means the underlying concern is retained with the limitations documented below; it does not endorse Claude’s exact replacement wording.

| Claude ID | Claim | Severity | Disposition |
| --- | --- | --- | --- |
| R-01 | TS-C01 | Major | Accept distinction; exclude cross-window numerical illustration |
| R-02 | TS-C04 | Major | Accept base-rate concern; qualify proposed controls and intervals |
| R-03 | TS-C05 | Major | Restore historical sensitivity results; retain unmeasured causal conclusion |
| R-04 | TS-C05 / experiment | Major | Accept design improvements; reject impossibility and byte-to-cost effect assumptions |
| R-05 | TS-C03 | Major | Correct mixed units; explicitly retain unresolved source-label conflict |
| R-06 | TS-C01, TS-C10 | Moderate | Accept classifier mixture; do not certify 89% day share or unconditional adjusted rate |
| R-07 | TS-C03, TS-C05 | Moderate | Accept cache risk and narrower audit; reject guaranteed two misses and modeled magnitude |
| R-08 | TS-C01 | Moderate | Accept meter/log distinction; add missing filters and historical scope |
| R-09 | TS-C01 | Moderate | Accept variability and commit-date correction; correct proposed daily range |
| R-10 | TS-C04 | Moderate | Accept key/annotation risks; distinguish suspected from proven misclassification |
| R-11 | TS-C07 | Moderate | Accept scope narrowing; correct “Anthropic only” byte-check claim |
| R-12 | TS-C08 | Moderate | Accept replay differences; distinguish fixed points from idempotence |
| R-13 | TS-C09 | Moderate | Accept predicate-positive interpretation and detector limits |
| R-14 | TS-C02, TS-C09 | Moderate | Accept omitted stages; specify post-cleanup controls and historical upstream cap |
| R-15 | TS-C02 | Minor | Accept conditional proof wording and indexing |
| R-16 | TS-C06 | Moderate | Accept explicit quantifier and practical scope; reject inherent untestability |
| R-17 | TS-C03 | Moderate | Retain hypothetical curves; reject aggregate replay anchor; report available Codex usage |
| R-18 | TS-C05 | Moderate | Clarify signed effects and quality; reject compulsory lossy-only G and invented quality price |
| R-19 | Presentation | Minor | Accept window, symbols and billing clarification; retain exact fractions and baseline filenames |
| R-20 | Interpretation | Moderate | Accept opportunity context; reject 89× mixed-unit comparison |

## Evidence and exact replacement decisions

### R-01 — Mechanical reduction is not a workload counterfactual

**Confirmed.** `TokenSaver/LlmProxyRelay.cs:145–156` meters the current body pair. An extra request caused by an earlier intervention enters both submitted and forwarded totals. Equation 1 is a valid mechanical byte total but not the difference between two complete task trajectories.

**Replacement, abstract and section 13:** “These compare forwarded bodies with the bodies submitted by the CLI on the observed trajectory. They do not measure total traffic or billing relative to a run without compression.” All observed rates remain unchanged. Claude’s 9 MB/12 MB illustration mixes a meter request mean with a different follow-up population and assumes one additional request per result; it is arithmetic under assumptions, not a new empirical bound. It is not carried into 0.3.

### R-02 — No comparison group supports an effect on behavior

**Confirmed with qualifications.** `books/python-scripts/token_saver/tiered_rerun_audit.py:204–240` permits named-file Reads, pause calls and basename/range matches; it does not identify causal recovery. `evidence.json/followup_counts` confirms every one of the 146 strict Codex cases is T2. The saved tier report lines 11–19 has 25/35 Claude cases in diff/show/log.

**Replacement, section 5:** “No comparable untruncated base rate was measured, so these are conditional classifier frequencies, not an estimated association or causal extra-turn rate.” The 6,589 zero-rewrite Codex meter requests on August 1–16 recalculate correctly. They do not establish clean uncompressed histories or comparable workloads: no new rewrite can coexist with previously compressed content, and eligibility/configuration can change. Threshold proximity alone does not validate regression discontinuity. Claude’s binomial Wilson intervals ignore clustering and classifier error; do not promote them to population uncertainty. Existing counts and unresolved-case bounds remain.

### R-03 — The omitted historical model belongs in context

**Confirmed omission; reject stronger causal conclusion.** `books/token_saver/research_2026-10-01.md:490–509` records -3.932M Claude and +18.105M Codex model units. Lines 518–524 and 657–662 say the price correction was Codex-only, but also explicitly reject causal net-token conclusions for either provider. `review_2026-10-01.md:309` recommends disabling Claude truncation. Current `CompressionCatalog.cs:182–185` still enables it.

**Replacement, section 11:** “Both modeled signs nevertheless depend on heuristic follow-ups being charged as whole additional turns, historical populations, character conversions and pricing assumptions. They are not measured net effects and their rates cannot be transferred to the newer tiered sample.” Restore model values and historical recommendation with attribution. Claude’s new q* estimate uses the invalid 12.6 appearance count from R-05, character-to-token assumptions and unmatched windows. Comparing it with 0.54 cannot establish net loss. A before/after trial can inform a pilot but remains confounded; no product change is authorized by the paper review.

### R-04 — Power is conditional, not impossible

**Partially accepted; the absolute replacement is unsupported.** The normal approximation for paired log-cost differences is `n ≈ [(1.96 + 0.842)σ / |log(1-s)|]²`. Recalculation yields about 176,247 pairs at s=0.002, σ=0.3; 489,575 at σ=0.5; 574–4,076 at s=0.0345, σ=0.3–0.8. Claude’s approximate ranges are reasonable on that assumed endpoint. They neither establish actual variance nor power for the paper’s ratio-of-total-costs endpoint. Gross byte percentage is not expected task-cost effect size. With sufficiently small paired variance, small effects are detectable; cost noninferiority also needs power and a margin.

**Replacement, section 12:** “These assumed values show possible cost, not impossibility, and the log-cost estimand differs from the ratio-of-total-costs endpoint below. Size the final endpoint and quality margin using pilot-based simulation or another justified method.” Define B by exact stage IDs, exclude `elide-passed-tests`, grade final artifacts with residual-blinding caveats, state the proxy-conditional estimand, freeze settings and address cross-arm cache reuse. Marker-bearing transcripts compromise blinding but do not prove all blinding impossible. Component token counting and fork-at-intervention are useful proposals with limited estimands, not observed results. No paid or free endpoint experiment was run.

### R-05 — The replay multipliers mix units

**Confirmed material correction, with provenance uncertainty.** Original `books/token_saver/truncation_file_reads.md:26–43` defines the meter tally as request bytes and the denominator as hash-deduplicated characters. Dossier lines 295–300 relabel the numerator as characters. Ratios 3,337,920/264,898=12.6007746 and 51,728,364/1,299,384=39.8099130 recalculate, but their units and deduplication do not identify appearances per block. The original scratch scripts were not retained (original report lines 218–219). Matching a rounded daily meter value is supportive, not reconstruction of the computation.

**Replacement, section 3:** “Replay, identical content across conversations, JSON escaping and encoding all affect the comparison. These pre-mitigation samples cannot supply m for a token-cost model.” Figure 1 now separates bytes and decoded characters. `evidence-v0.3.json` uses `reported_meter_tally_bytes`, `deduplicated_removed_characters`, and `bytes_per_deduplicated_character`, with the conflict explicit. Define model m to include the first appearance. Claude’s separate 1.26 “JSON escaping” attribution also exceeds evidence: the section 11 serialized-versus-tool-character delta may include other serialization changes; it is not an isolated escaping experiment.

### R-06 — Mixture is verified; day concentration is not matched

**Partially accepted.** September `summary.json/usage/(anthropic, claude-sonnet-5, large_no_tools)` independently gives 3,044 requests, 167,222,501 total input tokens, 4,634,900 one-hour writes and 381,468,024 decoded system characters. Shares 49.03%, 29.92%, 53.07% (of all one-hour writes), and 67.07% (of uncached input) recalculate. The identifying family is based on no tool definitions and a large system prompt; the saved report inspected five security-monitor examples. The aggregate does not itself prove all 3,044 contain no eligible historical tool results.

`research_2026-10-01.md:877` supplies 5,545 September 4 requests from a different usage extraction; the bounded summary has no per-day field. Dividing 5,545 by 6,209 is numerically 89.31%, but population equivalence is not established. Do not add that as measured fact. Decoded system characters are a conservative lower bound on serialized bytes, not the family body-byte total. The adjusted rate `3,889,891/(1,698,925,099-381,468,024)=0.2953%` requires zero removal in the excluded family.

**Replacement, section 10:** “If the family has zero eligible result removal, subtracting only that lower bound yields a conservative non-family reduction of at least 0.295%; exact family exclusion needs a matched body-byte subtotal and confirmation of zero result removal.” Retain the verified mixture in the Figure 9 caption; omit the unsupported 89% claim.

### R-07 — Pause transitions can affect caches

**Partially accepted.** `LlmProxySettingsService.cs:92–107` resolves effective enabled state per request, and `TokenSaver/README.md` describes two pause transition breaks. That README is a product warning, not an observed bill. Counterexample: if history contains no compressible result, pause and resume leave body bytes unchanged, so the pause introduces zero compression-related misses. A changed result after the last reusable prefix also need not invalidate the entire context.

**Replacement, section 3:** “A pause, resume or changed setting can alter previously sent result bytes and invalidate the affected cached suffix; two transition opportunities are not two guaranteed full-prefix misses.” Narrow the 64-case statement to Claude Window B, >50K writes, predecessor within five minutes (`research_2026-10-01.md:580–600`). Claude’s cost-erasure estimate again uses the invalid 12.6 appearance ratio, an aggregate cache ratio and assumed byte/token conversion; it is not a bound established by these sources. Leave cache effects signed and unmeasured in K.

### R-08 — Meter eligibility differs from archive selection

**Confirmed with additions.** `LlmProxyRelay.cs:140–158` requires nonnull savings and 2xx headers; lines 212–226 log independently. The Anthropic/Codex/Zai body transforms additionally check POST path, JSON Content-Type, detectable request body and a 10 MiB cap; `CliChatBodyTransform.cs` delegates to Responses/Chat transforms. Claude omitted content-type/body detection. Measurement happens before the response stream completes; 2xx is not proof of completed inference. Retries can be counted again. Content-Encoding is not decoded by these transforms.

**Replacement, section 2:** the complete current-source selection paragraph, followed by “current source inspection alone cannot certify every historical build’s filters.” Do not equate the September HTTP-200 archive with the longer meter population. This is a documentation correction, not an exposure/authentication change.

### R-09 — Rates vary and historical dates conflict

**Confirmed arithmetic; correct proposed caption.** From `daily-series.csv`: September 4 onward Codex range 0–12.14%, median 3.04%, unweighted mean 3.9518%, weighted 3.3936%; top five days 53.60% of removal. Claude top-five share is 57.08%. Codex removal before September 4 is 72.64%; August 17–29 is 60.67%. But August 17–29 daily rates span **0.94–28.21%**, including 5.03% and 9.91%; Claude’s proposed “15–28%” caption is false as a range. Also these bytes are not all attributed to defective file-read truncation.

`git show 2819e6ba` dates the mitigation commit to August 30, 2026, 01:49:54 -05:00. Original source annotations differ (August 29 versus August 30); neither pins rollout. **Replacement, Figure 4:** “Dashed lines mark the historical allowlist annotation and the August 30 file-read mitigation commit (2819e6ba), not verified deployment times or randomized interventions.” Public-meter byte/4 estimates remain a follow-up product-label issue; no counter/UI edits were made.

### R-10 — Keys, old behavior and harness notes need separate qualifications

**Partially accepted.** `scan_conversations.py:343, 436–443, 527–528, 661, 677–698` uses different opening-message material by protocol, provider `nokey`, and maximum-tool-count retention with message-count tie breaking. Anthropic hashes the first message, not specifically the first user message. The September miner has a separate preferred provider cache-key path. The documented 3/51 split does not quantify all collisions.

The tier report lines 25–29 contains the small `cat` truncation and two T0 examples beginning with “Only you see…”. `prompt-eval/extract_traces.py:66–67` identifies that prefix as harness text. The displayed excerpt does not contain the printed matched word “elided”; full notes are needed to decide its origin. It is not established that both matches are false. Likewise truncating that small `cat` is inconsistent with today’s budget but does not identify its build or settings.

**Replacement, section 5:** “Omitting this case gives 18/34 (52.9%) as a sensitivity calculation, not a corrected current-build estimate.” Keep the recorded bars and state the unresolved T0 annotation issue. A falling message count can also reflect compaction, branching or resume; it is a diagnostic lead, not proof of collision.

### R-11 — Tests are bounded, but Codex does check bytes

**Partially accepted; Claude’s blanket wording is contradicted.** `PipelineGoldenFixtureTests.cs:34–56` indeed runs `Condense(Minify(x))`, with nondefault ANSI/progress flags, no shape filters and no file-read selector. `validation.json/test_command` excludes Grok’s CliChat adapter and accounting/route classes. No new run was needed to amend a historical test claim.

However, `CodexResponsesRewriterTests.cs:35` asserts byte shrinkage; lines 56–58, 138–140 and 212 assert no-op byte counts and whole-body equality. Its changed-body tests also check preserved substrings. Thus “whole-body byte-identity … only for Anthropic” and “Codex … check parsed values” are too broad. Anthropic does have more direct adversarial escaping coverage (lines 151–264). **Replacement, section 7:** “Anthropic tests include adversarial escaping and changed-body byte checks; Codex also has whole-body no-op equality and shrinkage assertions, with narrower changed-body checks.” Bounded corpus property assertions exist; they are not generative fuzzing or a universal full-pipeline theorem. The exact 475 runtime count remains the saved run’s report, not a newly certified discovery count.

### R-12 — Historical inputs differ from current fixed points

**Confirmed and independently recounted.** `report_recent.py:19–20` exports `After`; `replay_pipeline/Program.cs:23–43` tests repeated application with the same metadata. Recounting saved JSONL gives 8,177 rows × 4 = 32,708 cases, zero character growth, negative byte deltas, nondeterminism or non-idempotence flags. Comparing saved input/output using the allowlisted tool set gives 45 changed after-texts: 44 whitespace-only, one added truncation and 5,977 UTF-16 units removed. No private transcript text is copied into this report. The 28 multi-version groups are already present in 0.2 section 8, contrary to Claude’s “omits” wording; they remain a saved-report result, not a fresh raw-group recount.

**Replacement, section 8:** “Idempotence means P(P(after)) = P(after), which does not require P(after) = after.” Historical differences can result from builds, settings, pauses, command metadata or byte-gate decisions. They are not proof of nondeterminism, and multi-version raw groups can differ in context. Preserve zero-flag results and qualify their scope.

### R-13 — Detector-positive rate is not recall

**Accepted.** `truncation_file_reads.md:180–199` samples all unique elided outputs, not a labeled file-read ground truth. `CommandShape.cs:196–199` omits `awk`; the saved tier report has three Claude `awk` T2 cases, including a pause. `OutputCondenser.cs:166–170` chooses the wider budget only below the post-dedupe length ceiling.

**Replacement, Figure 8:** “This is a predicate-positive rate on all sampled elided outputs, not recall or precision on a labeled file-read set, newly saved tokens or a task-quality effect.” Add known limits without calling related calls proven recovery or the whole widened budget semantic preservation. 205/261, 217/288 and associated character shares remain correct.

### R-14 — Describe the actual pipeline, after cleanup

**Accepted with two qualifications.** `CompressionCatalog.cs:123–185, 201–243` confirms default CRLF normalization, three grouping stages, passing-test elision, broad shell/background allowlists and disabled Read/Grep. `CompressionPipeline.cs:113–137` runs cleanup before shape/condensation, so “any output containing CR” would wrongly exclude default-normalized CRLF output. `ShapeFilters.cs:101–111` and `OutputCondenser.cs:144–148` check surviving control characters.

**Replacement, section 1:** “Shape and condensation stages decline text still containing ESC, BEL or CR after cleanup.” Describe ordinary user text versus tool_result blocks, passing-test elision and the shell/background scopes. The ~30K Claude Bash cap is source-reported for the historical harness, with 29,934 observed; it is not a universal current-client guarantee. Correct source-line citations without changing code.

### R-15 — Splice proof holds under its assumptions

**Accepted.** The three strict gates are `AnthropicMessagesRewriter.cs:188`, `CodexResponsesRewriter.cs:209`, `ChatCompletionsRewriter.cs:176`. Cursor copying and original-body fallback support the model, but this is not formal verification of parser correctness or all exception behavior. Anthropic’s emoji rejection fixture supports the encoded-length distinction; accepting a shorter span can still re-escape its retained characters.

**Replacement:** abstract “A proof under a splice model, checked against the three rewriters by source inspection, establishes conditional byte non-growth.” Equation 3 now explicitly has n eligible spans and n+1 untouched spans. Add the cache-cost limitation. The mathematical non-growth result remains valid.

### R-16 — The theorem is valid and not inherently untestable

**Reject the absolute claim; accept practical limits and quantifier.** For a finite domain `{0,1}`, take P(x)=x and g(x)=x: every fiber and answer can be exhaustively checked. A constant g also factors through any P by construction. Thus “the condition cannot be tested in practice” is not a general consequence of the theorem. The existing counterexample for omitted middle facts remains valid for unrestricted lossy truncation. Changing the theorem heading to an impossibility slogan would discard its useful fixed-task characterization.

**Replacement, section 7:** “It can be checked on a finite fully specified domain or proved analytically for a known task function; arbitrary real-world task domains are not exhaustively testable from traffic.” Add explicit `∀x,y`, use P for the compressor and distinguish interactive recovery from static decoding. Only a task experiment can estimate the proposed deployment effect; it is not the only conceivable method of proving a restricted preservation property.

### R-17 — Usage ratios do not identify removed-block replay counts

**Partially accepted.** The existing curves explicitly declare hypothetical α and w; no requirement says each must correspond to a studied provider. Retain them as parameter sensitivity, not price quotes. The input durations imply 46.65% one-hour writes and a hypothetical duration-weighted w≈1.60 if the assumed 1.25/2 prices apply. Historical expiry cases do not prove all removed blocks experienced expiry. More generally, sum actual appearance rates rather than assuming every later appearance is a read.

Claude’s proposed `539,740,748/18,718,783 ≈ 28.83` replay anchor is invalid for m: it mixes all content, blocks, write events, expiry, uncached tokens and heterogeneous histories. Also, Codex cache totals **are available without a database query** in the saved `summary.json/usage`: 168,536,064 cached tokens out of 176,018,836 input tokens = 95.7489%, for the usage-present subset (100 of 1,843 responses lack usage). Cached tokens are included within total input. This is not an extra bucket or a full-population cache-hit probability.

**Replacement, sections 3/10:** keep hypothetical rates explicitly conditional, generalize to actual applicable appearance rates, and report the available Codex subset with its missing-data limitation. Current provider pricing was not independently retrieved; do not assert unverified universal provider exceptions or current prices.

### R-18 — Keep monetary and quality outcomes separate

**Partially accepted.** G can cover any consistently defined task/intervention population; it need not exclude cleanup benefits just because recovery risk is concentrated in lossy events. If per-event quantities are averaged, expectations and recovery conditioning must be coherent. K may be negative, and must not overlap recovery costs. Silent failures matter, but a monetary F for quality degradation is not defined or estimated here; simply adding rF mixes endpoints without a valuation.

**Replacement, section 6:** define the unit, use H for all conditional recovery cost in that unit, permit signed K, and state: “It concerns monetary cost, not task utility: silent quality loss remains possible even with no recovery. Quality is a separate outcome in section 12.” Keep Figure 6 explicitly hypothetical with H/G notation. Do not plot Claude’s 2.1–2.7 range as evidence: its construction inherits the mixed-unit ratio and causal assumptions rejected above.

### R-19 — Presentation refinements are optional, not statistical repairs

**Accepted in part.** State the meter window in the abstract. Use P for compression, H for recovery cost and L for task monetary costs. Keep old figure filenames for the unchanged baseline; new files carry the 0.3 directory and figure numbers. Percentages such as 19/35=54.29% are exact arithmetic of a census of classified instances, not a precision claim about an underlying population; counts plus the explicit no-population-CI statement are defensible. No invalid binomial confidence interval is added.

**Replacement, section 12:** “API-priced usage, cash expenditure under a flat subscription and quota consumption are distinct outcomes; the workstation’s billing regime is not established by the package.” The full-window rounded rates 0.3777% Claude and 5.8625% Codex recalculate but are not substitutes for the prespecified primary window.

### R-20 — Do not repeat the units error for other opportunities

**Reject the 89× wording; retain attributed context.** `codex-mine-0908/findings.md:42–45` gives 347,502,249 **compact-JSON characters** of definitions with no correlated call. The comparator is 3,889,891 **request bytes**. Their quotient 89.3346 has characters/byte units, not a dimensionless opportunity or savings factor. Unused-in-observed-history definitions are not automatically safe to remove. The MCP disconnect’s ~60% comparison (`research_2026-10-01.md:592–597`) is an old modeled cost comparison, not measured compression harm.

**Replacement, section 13:** “This is an opportunity ceiling, not removable bytes or measured savings; it cannot be divided by the 3,889,891 removed bytes to claim an 89-fold effect.” Keep the numerical quantities separately and the disconnect as historical context.

## Verification of Claude’s additional checks

- All 18 original manifest SHA-256 values match; Markdown and Word match `review-baseline.json`.
- Structural reading of the unchanged Word confirms Track Changes enabled and six native equations. The unchanged PDF has 16 pages. Extracted sets of numbers of at least three characters match Markdown/Word/PDF under the recorded regex check. This supports numeric text consistency, not that every occurrence, equation or image agrees, and is not new visual layout QA.
- Primary table arithmetic, tier sums, unresolved-case bounds, the 3,150-unit worked example and the 5.15 illustrative cache multiplier check out. Decimal units are consistent with the rounded meter table. “All exact” must not imply reconstruction of unrounded original meter measurements.
- The daily weighted rates reproduce 0.192284% and 3.393585%. The source-label conflict remains despite these correct calculations.
- Saved replay flags were independently recounted, including canonical-byte differences. Exceptions=0 remains the historical completed report’s statement; no new C# execution occurred.
- Current source confirms thresholds, aggregate byte/4 integer division and the conditional splice gates. The 475-pass result remains the historical `validation.json` record, not a new run or formal proof of coverage.
- No product code changed in this review. Unrelated concurrent Board edits were outside scope. Git cleanliness of the original reviewer’s earlier session cannot be reconstructed from today’s working tree; file hashes identify the source content actually checked.

## Sources inspected and boundaries

**Inspected:** full 0.2 Markdown and Claude review; both Board Markdown attachments; REVIEW/README; evidence, daily CSV, source/baseline/validation/discovery manifests; unchanged DOCX XML and PDF extracted text; original figures as needed for revisions. All 18 source-manifest files were hashed; substantive inspection covered the three rewriters, OutputCondenser, CompressionCatalog, ITokenSavingsStore, the dossier, October review, file-read report, September findings/summary/replay flags, replay helper and relevant scanner/classifier logic. Additional source inspection covered body transforms, relay, settings, pipeline, CommandShape/ShapeFilters, focused test implementations, report_recent.py, saved tier report, saved replay input/output comparisons and harness-prefix identification. Historical corrective ANSI and capture-limit findings were checked in the dossier/discovery and matching manifest sources; their underlying capture populations were not recollected. Relevant commit metadata was inspected.

**Unavailable or not independently inspected:** Study A’s unpreserved scratch scripts; raw database populations (`state.db`, `proxy_exchanges.db`, `mining_timeline.db`, `recent.db`) deliberately unopened; full T0 notes beyond saved excerpts; matched per-day/byte subtotals needed for R-06; executing build/configuration for individual historical truncations; current external provider schedules; the workstation’s actual billing agreement; new runtime test discovery/execution; visual layout of every unchanged Word/PDF page. Saved JSONL comparisons are read-only source inspection, not live traffic collection. No source hash is treated as evidence of inspecting all its contents.

No claim ID was left unexamined. TS-C02 and TS-C06 retain valid mathematical results after clearer scope; TS-C01, C03–C05 and C07–C10 retain measured or reported quantities with corrected units, populations and limits. Net complete-task billed savings with preserved quality remains unmeasured.

## Concurrent technical-note chart draft

During this review, a separate untracked `note/make_charts.py` and two SVGs appeared in the shared checkout. They were not present at the start. I first inspected the generator as an additional snapshot of current work; no accompanying note manuscript was present at that initial inspection. It remains separate from the 0.3 manuscript.

The HTML manuscript subsequently appeared and was checked in `review-notes-codex-technical-note.md`. After automated review corroborated its blocking unit/power findings, the supported corrections were applied as technical note 1.1, its SVGs/PDF regenerated and all 11 PDF pages visually checked. `technical-note-review.diff` now records the applied source changes from the inspected 1.0 snapshot. The supplement records both the original findings and their resolution.

The daily weighted-period arithmetic and missing-day gaps are consistent with the CSV. The chart’s “fixed 30 Aug” band should say “mitigation commit 30 Aug; deployment not verified” for the same reason as R-09. The value formula `(w + alpha*(m-1))/m` is correct per **actual token appearance** under one write and subsequent cache hits. A “counted token” from the product’s byte/4 heuristic need not be an actual model token: if d actual tokens correspond to b removed bytes, the factor per meter-estimated token is `(4d/b) * (w + alpha*(m-1))/m`. The chart therefore needs an explicit calibration assumption or a label stating actual tokens, along with unchanged continuation/cache assumptions. Its provider-labeled prices require a pinned model/date and pricing source; the generic algebra alone does not validate those labels. This feedback applies to the inspected chart snapshot, not to later concurrent edits.
