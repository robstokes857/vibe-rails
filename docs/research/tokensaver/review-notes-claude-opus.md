# Review notes: TokenSaver paper 0.2

- **Reviewer:** Claude (claude-opus-5-5), Claude Code desktop session, 2026-10-05
- **Baseline:** TokenSaver paper 0.2. `tokensaver-research-review.md` SHA-256 `7fbbd891…a76df` and the
  Word copy `0d50e9ae…b2d4` both match `review-baseline.json`.
- **Method:** source inspection and arithmetic only. No database was opened, no extraction or audit
  script was executed, no builder was run, no paid inference was used, and the manuscript was not edited.
  Two read-only sub-audits were delegated, one of the product source and one of the vibe-books sources.
  Every figure taken from them was re-read at the cited line, except where a finding marks it
  *(sub-audit)*.
- **Status:** this is a review, not an independent replication. All revisions below are proposals pending
  reconciliation.

## Summary

| ID | Claim | Severity | Assessment | Finding |
| --- | --- | --- | --- | --- |
| R-01 | TS-C01 | Major | Needs qualification | "Reduced bytes in the workload" uses a post-treatment baseline. For Claude, the sign of the workload-level effect is not established. |
| R-02 | TS-C04 | Major | Unresolved | The follow-up rates have no base rate. T2 counts ordinary post-diff work as "strict". A pass-through control period already exists in the data. |
| R-03 | TS-C05, §13 | Major | Needs qualification | The paper omits the dossier's negative Claude sensitivity result and its recommendation to disable Claude truncation. The stated reason for omitting them (the price correction) applied to Codex only. |
| R-04 | §12 | Major | Needs qualification | The proposed experiment cannot detect 0.2% effects. Arm B, blinding and arm A need definitions. |
| R-05 | TS-C03 | Moderate | Contradicted | 12.60x and 39.81x divide a meter byte tally by globally deduplicated characters, from pre-fix samples. They are not Σ m_i·d_i. |
| R-06 | TS-C10, TS-C01 | Moderate | Needs qualification | The September audit is 89% one day and 49% auto-mode safety-classifier requests. |
| R-07 | TS-C03, TS-C05 | Moderate | Needs qualification | Stable rewriting is broken on purpose by pause and settings, and one cache break can cost about as much as Claude's whole gross saving in the window. The 64-miss evidence is narrower than stated. |
| R-08 | TS-C01 | Moderate | Needs qualification | The meter measures a narrower population than "request bodies", one that differs from the Section 8 archive by construction. |
| R-09 | TS-C01 | Moderate | Needs qualification | Rates are unstable and concentrated. Most Codex history comes from the defective pre-fix configuration. The public counter counts re-sends. |
| R-10 | TS-C04 | Moderate | Needs qualification | Conversation keys collide and split. The "post-fix" window contains a pre-fix truncation. T0 notes contain harness text. |
| R-11 | TS-C07 | Moderate | Needs qualification | The tests are narrower than the Section 7 table says. The meter tests and Grok adapter tests were not run. |
| R-12 | TS-C08 | Moderate | Needs qualification | The replay took already-compressed after-texts as input. The paper omits 45 non-fixed-point outputs and 28 multi-version groups. |
| R-13 | TS-C09 | Moderate | Needs qualification | 205/261 is a positive rate, not recall. `awk` file reads are missed (3 truncated Claude cases, each followed by a related call). There is a 262,144 cliff. |
| R-14 | §1 | Moderate | Needs qualification | The mechanism table omits a default-on lossy stage, the control-character skip, the allowlist breadth and the 30K upstream cap. Several citations have drifted. |
| R-15 | TS-C02 | Minor | Needs qualification | Proposition 1 holds. "Source-level proof" overstates it, and smaller bodies are not necessarily cheaper. |
| R-16 | TS-C06 | Minor | Needs qualification | Proposition 2 is correct but untestable. Reframe it as a limit. Equation 5 lacks a quantifier. |
| R-17 | TS-C03 | Minor | Needs qualification | The illustrative cache parameters don't match the providers studied, though the paper's own data could anchor them. |
| R-18 | TS-C05 | Moderate | Needs qualification | The break-even model has no silent-failure term, no negative K, and an unanchored C/G. |
| R-19 | — | Minor | Presentation | Figure-file numbering, the overloaded symbol C, false precision, a missing window in the abstract, the billing regime and proof indexing. |
| R-20 | §13 | Minor | Needs qualification | The interpretation lacks scale against other levers. |

Every claim ID, TS-C01 through TS-C10, has at least one finding. The checks that passed are listed at the end.

---

## Major findings

```text
Finding: R-01
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: TS-C01
Severity: Major
Assessment: Needs qualification
Location: §13 para 1: "The evidence supports a precise claim: TokenSaver reduced transmitted
  request-body bytes in the observed workload, with different rates by provider." Abstract
  claim table: "Smaller transmitted request bodies | Measured; provider and snapshot specific".
Problem: What was measured is per request: the forwarded bytes A_j against the body B_j that
  the CLI built in a run where TokenSaver was already active. B_j is a post-treatment baseline.
  Follow-up reads caused by earlier truncation appear in both B_j and A_j, along with the
  content they bring back, so D_B never subtracts them. "Reduced bytes in the workload" reads as
  a counterfactual claim, fewer bytes than without TokenSaver, and that is not measured. The
  paper concedes this for tokens but not for bytes.
Evidence: The meter records BytesBefore − BytesAfter per request
  (TokenSaver/Minify/ToolOutputRewriteResult.cs:13; AnthropicMessagesRewriter.cs:250-251).
  A sensitivity check using the paper's own aggregates: the mean Claude body in the Figure 3
  snapshot is 7.71 GB / 16,238 = 474.8 KB. If each of the 19 strict Claude follow-ups were a
  causally additional request, those requests alone would transmit about 9.0 MB, which is 59%
  of the 15.4 MB removed. With the 26 loose follow-ups it is about 12.3 MB, or 80%. This is
  before counting the recovered content's own re-sends. For Codex: 146 × 611.9 KB ≈ 89 MB,
  26% of the 343 MB removed. The windows differ (the follow-up audit covers keys first seen on
  or after Aug 30, retained timelines only; the meter covers Sept 4–Oct 1), so this is an
  order-of-magnitude illustration, not an estimate.
Proposed revision: §13: "The evidence supports a precise claim: in the observed requests,
  accepted rewrites made each forwarded body smaller than the body the CLI submitted, by 0.20%
  (Claude Code), 3.45% (Codex CLI) and 0.18% (Grok CLI) of submitted bytes in the September 4 to
  October 1 meter window. Submitted bodies already contain any follow-up reads caused by
  earlier compression, so these shares are per-request mechanical reductions, not the
  reduction relative to running without TokenSaver." Claim table row: "Forwarded body smaller
  than the submitted body | Measured per request; workload-level effect not measured".
Effect: Abstract (claim table and first-paragraph rates), §13, any public wording.
Remaining uncertainty: The causal fraction of follow-ups, and whether they were extra round
  trips. Resolving this needs R-02's control or R-04's experiment.
```

```text
Finding: R-02
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: TS-C04
Severity: Major
Assessment: Unresolved
Location: Abstract: "The corrected follow-up audit finds a related call in the next assistant
  turn after 19/35 Claude truncations and 146/448 classified Codex truncations. These
  associations do not establish that truncation caused extra turns." §13: "Claude's gross
  reduction is small in this window, while related follow-ups are common among its truncated
  results. Codex removes more data but shows a substantial diff-related follow-up signal."
Problem: There is no comparison group, so these rates are neither associations nor a signal.
  Nothing shows that the classifier fires less often after comparable untruncated outputs.
  The T2 rule also counts ordinary post-diff work as "strict":
  - a Read of any file whose basename appears in the truncated command;
  - a shell call sharing one basename plus any range-like token (head -n, tail -n, -C N, or
    the bare words "offset" or "limit").
  Every one of the 146 strict Codex cases is T2 (T0 = T1 = 0). 25 of the 35 Claude cases are
  git diff/show/log, after which opening the diffed files is normal work.
Evidence: tiered_rerun_audit.py:67-68 (RE_RANGE); :204-225 (the relation function, where a
  basename-only Read returns "read" at :212-214); :239-240 (read, pause and strict all map to
  T2). evidence.json followup_counts.
  results/vibe6-paper/tiered_rerun_2026-08-30_20261001_201359.md:11-19 (25 of 35 Claude cases
  are diff/show/log). The dossier itself says "No randomisation, no control arm"
  (research_2026-10-01.md:643).
  Controls already exist in captured data:
  (a) daily-series.csv shows Codex rewritten = 0 on all 16 days Aug 1–16 (6,589 requests), a
      pass-through period in which outputs the current activation rule would have truncated can
      be identified deterministically;
  (b) comparable untruncated outputs from any period;
  (c) outputs just below the activation threshold, for a regression-discontinuity comparison.
  Wilson 95% intervals, before any clustering adjustment: 38–70% (19/35), 28–37% (146/448),
  37–49% (122/284).
Proposed revision:
  Abstract: replace the two sentences with "A heuristic classifier finds a related call in the
  next assistant turn after 19 of 35 Claude truncations and 146 of 448 classified Codex
  truncations. No comparison group of untruncated outputs was analysed, so these rates are not
  evidence that truncation changed agent behaviour."
  §5, add: "Without a base rate these frequencies are not associations. The same classifier
  should be applied to Codex outputs from August 1–16, when no Codex request was rewritten,
  that the current activation rule would have truncated; to comparable untruncated outputs;
  and to outputs just below the activation threshold."
  §13: replace "while related follow-ups are common among its truncated results" and "shows a
  substantial diff-related follow-up signal" with "the follow-up classification has no base
  rate and cannot yet distinguish recovery from ordinary next steps."
Effect: Abstract, §5, the interpretation of Figure 5, §13.
Remaining uncertainty: The base rates are computable from existing captures but need a
  read-only database query, which is a separate task.
```

```text
Finding: R-03
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: TS-C05 (and §13)
Severity: Major
Assessment: Needs qualification (conflicting evidence omitted)
Location: §11 Threats: "Historical cost models used a loose classifier and a subsequently
  corrected output-price factor. Their net estimates are not reproduced as results here."
  §13: "Net token economics remain unresolved."
Problem: The paper drops the source dossier's interpretation and recommendation. Its stated
  reason for omitting the cost-model results does not apply to Claude, because the
  output-price correction was Codex-only. The omitted material points in opposite directions
  for the two providers. Calling the economics "unresolved" for both hides the most
  decision-relevant evidence. The review brief asks reviewers to report conflicting evidence
  rather than silently select.
Evidence:
  research_2026-10-01.md:657-658: "Codex 8× corrected to 5×/6×; Claude 5× not re-checked per
  model".
  Study B (:490-510): Claude net −3,932K cost-equivalent tokens, with break-even at about 19%
  re-runs against 75% observed (the strict variant gives 40% and is still −3.3M). Codex net
  +18,105K. "Under this model's assumptions the meter was reporting a loss as a win."
  §10 (:677-689): "For Claude Code the evidence favours H2 over H1; it does not prove either …
  strong enough to justify disabling or A/B-testing truncation on Claude." "For Codex CLI the
  evidence is consistent with H1."
  §13.1 (:770-772): with Claude truncation off, "the Claude meter should fall by most of its
  0.20 %".
  review_2026-10-01.md:309: "Recommendation: disable `truncate-long` by default for
  `anthropic`."
  An independent bound from this paper's own aggregates points the same way. Assumptions: 3–4
  characters per token, m = 12.6, w = 1.25, α = 0.1, output at 5× input.
  - Per Claude truncation, about 318K / 35 ≈ 9.1K characters are removed (2.3–3.0K tokens),
    so G ≈ 5.5–7.3K base units.
  - A recovery that adds one round trip, at the September audit's mean context (90,009 input
    and 595 output tokens), costs about 9.0K + 3.0K + 2.6–3.5K for the replayed recovered
    content. That gives C ≈ 14.6–15.5K, so q* ≈ 0.38–0.47.
  - That is below the observed strict rate of 0.54.
  - If the recovery call is bundled into a turn that happens anyway, q* ≈ 2.1.
Proposed revision:
  §11, replace the two sentences with: "Historical cost models are sensitivity analyses, not
  measurements. Under the dossier's Study B model (loose v1 classifier, whole-turn charge,
  provider-wide prices, pre-fix population), Claude truncation was net negative (−3.9M
  cost-equivalent tokens; break-even re-run rate ≈19% against 40–75% observed) and Codex was
  net positive (+18.1M). The later output-price correction applied to Codex only. The source
  dossier concluded that the Claude evidence favours illusory or negative savings, and the
  October 2 review recommended disabling truncation for Claude by default. That change has not
  shipped."
  §13: "Net token economics are unmeasured. The available sensitivity analyses lean negative
  for Claude truncation and positive for Codex. The cheapest next test is the Claude
  truncation-off before/after comparison proposed in the dossier."
Effect: §11, §13, and the abstract sentence "It does not yet establish net billed-token
  savings".
Remaining uncertainty: Every cost model here rests on unmeasured causal recovery rates and on
  per-model prices.
```

```text
Finding: R-04
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: §12 (no claim ID; examined at the brief's request), TS-C05
Severity: Major
Assessment: Needs qualification
Location: Abstract: "We derive the break-even conditions and specify a paired experiment that
  can test both". §12.
Problem: As specified (complete tasks, randomised arm order, total task cost as the primary
  outcome), the experiment cannot detect effects the size of Claude's or Grok's gross
  reduction. For Codex it would need thousands of task pairs. Independent agent runs diverge
  after the first differing token, so the cost variation between runs dwarfs a 0.2% effect.
  Several design details also need fixing.
Evidence: Sample sizes for a two-sided 5% test at 80% power on paired log-cost differences.
  The SD range is an assumption the pilot must measure.
  - 0.2% needs about 176,000 task pairs at SD 0.3 and about 490,000 at SD 0.5.
  - 3.45% needs about 590–4,200 pairs at SD 0.3–0.8.
  - A 1,000-pair study detects 0.2% only if the SD is below about 0.02.
  Design issues:
  (a) Arm B ("cleanup/shape without lossy condensation") is ambiguous. The default-on stage
      "Collapse passing tests" is classed Lossy (TokenSaver/Pipeline/CompressionCatalog.cs:
      170-176) and is neither "cleanup" nor "condensation" as §1 describes them.
  (b) Blinded transcript grading is impossible, because the `[... N lines elided ...]` markers
      (OutputCondenser.cs:90-91) reveal the arm. Graders should see only the final repository
      state and checks.
  (c) Arm A is not a no-VibeRails baseline. The proxy strips Accept-Encoding
      (LlmProxyRelay.cs:48-53), Claude launches with ENABLE_TOOL_SEARCH=true
      (LlmProxyClaudeConfig.cs:30-38, sub-audit), and Codex runs under a custom model_provider
      (LlmProxyCodexConfig.cs:56-72, sub-audit). That is fine for the rewriting contrast, but
      the estimand must be stated as "rewriting, conditional on the proxy".
  (d) Arms that share a system prompt, tools and repository snapshot also share the provider
      prompt cache up to the first divergent tool result, so arm order decides who pays cache
      writes.
  (e) Each pause costs two cache breaks (R-07), so pause_token_saver must be disabled or
      logged per arm.
Proposed revision:
  Abstract: "… and specify experiments that can bound net cost and test quality; a
  complete-task comparison can detect only effects of several percent."
  §12, before the table: "Power. Gross byte reductions of 0.2% (Claude) and 0.18% (Grok) are
  far below what any feasible complete-task comparison can detect. For those providers the
  task experiment can establish cost noninferiority (an upper bound on any cost increase) and
  quality noninferiority, not a positive saving. Two cheaper designs isolate the components:
  (1) exact gross token differences per request (D_T, equation 2), found by counting tokens
  for stored original and forwarded bodies with the provider's token-counting endpoint, or the
  published tokenizer where none exists; (2) a fork-at-intervention design, which branches
  each recorded conversation at a truncation event and runs the next k turns with the full and
  the truncated output from the same cached prefix, measuring follow-up calls, k-turn cost and
  the answer."
  In the table: define arms by stage IDs; state that graders see only the final repository
  state; record pause events and actual cache usage per arm.
Effect: Abstract, §12.
Remaining uncertainty: The real between-run variance, which needs the pilot the paper already
  proposes.
```

## Moderate findings

```text
Finding: R-05
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: TS-C03
Severity: Moderate
Assessment: Contradicted (definition and units)
Location: §3: "For distinct removed result content i, let d_i be the removed character count
  and m_i its number of appearances in subsequent requests. Under stable rewriting, D_unique =
  sum d_i and D_wire,char = sum m_i d_i. Their ratio is a removal-weighted mean replay count".
  Figure 1 caption: "character counts with and without replay weighting". Abstract: "Repeated
  history explains why wire removal exceeds distinct-content removal by 12.60x and 39.81x".
Problem: The numerators of 12.60x and 39.81x are not Σ m_i·d_i in characters. They are the
  meter's whole-request byte tally (BytesBefore − BytesAfter summed over every request that
  day). The denominator is decoded characters, deduplicated by content hash across the whole
  capture database. So each ratio:
  - mixes units, with JSON-escaped UTF-8 bytes over characters;
  - counts identical outputs from different conversations once in the denominator;
  - includes every proxied request that carried the output, such as sub-agents and
    compactions.
  Both samples are also pre-fix: 65% of Claude's distinct removal that day came from file-read
  elisions the mitigation later stopped. The computation scripts were not kept. Separately,
  "m_i … in subsequent requests" conflicts with the cache formula d[w + α(m − 1)], which counts
  the first appearance in m.
Evidence:
  truncation_file_reads.md:26-28: the meter records BytesBefore − BytesAfter for the whole
  body, per request.
  :36-38: the column is "tally (what the meter counts)", "De-duplicated by tool-output hash
  across ~/.vibe_rails/proxy_exchanges.db".
  :218-219: "The measurement scripts were scratch, not shipped".
  research_2026-10-01.md:297 relabels the same numbers "meter tally (chars)". The Claude
  numerator, 3,337,920, equals the 2026-08-28 Claude meter value of 3.34 MB in
  daily-series.csv.
  truncation_file_reads.md:54-55, 73-74: 27 of Claude's 33 lossy elisions that day were file
  reads, totalling 173,248 of 264,898 distinct characters.
  The fix reached main on 2026-08-30 (sub-audit: commit 2819e6ba), after the Aug 28–29
  samples.
  JSON escaping alone inflates a serialized delta over a decoded delta by about 1.26x in the
  paper's own §11 row: 0.23% × 6,154 MB = 14.2 MB against 11.2 MB.
Proposed revision:
  §3: "Study A compared the meter's byte tally for one day (whole-request BytesBefore −
  BytesAfter, summed over every request) with the decoded characters of content-hash-distinct
  removed outputs. The ratio, 12.6 for Claude on August 28 and 39.8 for Codex on August 28–29,
  combines replay within conversations, duplicate outputs across conversations and JSON
  escaping, and was measured before the August 30 file-read mitigation. It shows that the
  meter counts re-sends; it is not a replay count."
  Figure 1 caption: "Historical Study A, pre-mitigation: meter byte tally versus
  content-hash-distinct removed characters (mixed units)."
  Rename evidence.json `wire_chars` to `meter_tally_bytes`. Define m as the total number of
  requests that carry the block, including the first.
Effect: The abstract sentence, §3, Figure 1, evidence.json, and any use of 12.6 or 39.8 as m.
Remaining uncertainty: The exact Study A computation cannot be re-derived without the scratch
  scripts or a database query.
```

```text
Finding: R-06
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: TS-C10, TS-C01
Severity: Moderate
Assessment: Needs qualification
Location: §10: "The following values cover the 6,209 Claude requests in Figure 7, including
  main and side requests." The §8 table (Claude 0.229%) and Figure 9.
Problem: One afternoon and one side-channel request family dominate the bounded audit, and
  the paper reports neither.
  - 89% of its Claude requests are from September 4.
  - 49% (3,044) are Claude Code's auto-mode safety-classifier requests, which have no tools
    and a system prompt of more than 100,000 characters.
  - That family has nothing to compress, yet it carries 29.9% of input tokens, 53.1% of
    one-hour cache writes, 67.1% of uncached input and at least 22.4% of request-body bytes.
  Figure 9 therefore describes this mix, not ordinary coding sessions. The byte rate is
  diluted in the opposite direction: excluding the classifier raises Claude's reduction from
  0.229% to at least 0.295%.
Evidence:
  codex-mine-0908/findings.md:77-90: 3,044 requests, 49.0%. Family usage: uncached 273,960;
  cache creation 4,634,900; reads 162,313,641; total 167,222,501 of 558,868,027.
  summary.json usage key ("anthropic","claude-sonnet-5","large_no_tools"): system_chars =
  381,468,024, which is 22.45% of 1,698,925,099.
  research_2026-10-01.md, appendix A.3: 5,545 Claude requests on 09-04, with 35 large cache
  misses and 3.95M write tokens.
  3,889,891 / (1,698,925,099 − 381,468,024) = 0.295%.
  The meter's Claude denominator is diluted the same way: in the dossier's appendix A.4,
  classifier bodies are 898.5 MB of about 7,029 MB of Claude request bodies in Window C.
Proposed revision:
  §10, after the first paragraph: "This population is concentrated. 89% of its Claude
  requests occurred on September 4, and 49% (3,044) are Claude Code's auto-mode
  safety-classifier requests, which carry no tools or tool results. That family accounts for
  29.9% of input tokens, 53.1% of one-hour cache writes and at least 22.4% of request-body
  bytes. None of its bytes can be compressed, so excluding it raises Claude's reduction from
  0.229% to at least 0.295%."
  Figure 9 caption, add: "Dominated by September 4 and by safety-classifier requests; not a
  profile of ordinary sessions."
Effect: Interpretation of the §8 Claude row, §10, Figure 9, and any use of these buckets as
  a cost profile.
Remaining uncertainty: A per-session breakdown needs recent.db.
```

```text
Finding: R-07
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: TS-C03, TS-C05 (and the §7 cache-miss sentence)
Severity: Moderate
Assessment: Needs qualification
Location: §3: "This identity assumes unchanged future requests, token counts, cache hits and
  cache boundaries." §6: "K other incremental costs, including cache changes". §7: "zero
  compression-attributed divergences in 64 investigated close-in-time cache misses".
Problem: The paper treats stable rewriting as an assumption, but the product breaks it on
  purpose, and each break is large next to Claude's savings.
  - The plan and pause state are re-read on every request, and every tool result in history
    is recompressed each turn.
  - So a pause, a settings change or an upgrade changes bytes that were already sent, which
    breaks the provider prefix cache. The product's own README says each pause costs two
    cache breaks.
  - Paused requests are not metered at all.
  The replay study also found direct evidence of instability that the paper omits (R-12). And
  the 64-miss evidence is narrower than the paper states.
Evidence:
  LlmProxySettingsService.cs:16-22 and 92-107: settings are resolved per request
  (`!tokenSaverPaused && …`).
  TokenSaver/README.md:404-407: "A pause costs two prompt-cache breaks … once when the pause
  starts and again when it ends."
  LlmProxyRelay.cs:145-146: a request is measured only when a transform ran and the response
  was 2xx.
  Magnitude:
  - One full-prefix rewrite at the September audit's mean Claude context (90,009 input
    tokens) costs up to about 104K base units at the 5-minute write price, or about 171K at the
    one-hour price.
  - The whole window's gross Claude removal is worth about 0.13–0.25M base units: 3,889,891
    bytes ÷ 3.04–4 bytes per token, times a per-appearance weight of 0.14–0.19 at m =
    12.6–29.8.
  - So one or two compression-induced breaks would erase that window's gross saving.
  Scope of the 64 (research_2026-10-01.md:234-240, 583-600):
  - Claude only, Window B only.
  - Misses over 50K cache-write tokens with a same-conversation predecessor within five
    minutes.
  - About 59 are itemised.
  - Window C's 56 close-in-time misses were never diffed, and Codex was not examined.
  The 108-combination sweep used synthetic inputs (:242-247).
Proposed revision:
  §3, after the identity: "Stable rewriting is not guaranteed in practice. TokenSaver re-reads
  its settings and pause state on every request and recompresses the whole history, so a
  pause, a settings change or an upgrade changes bytes that were already sent and breaks the
  provider's prompt cache. The product documents two cache breaks per pause. Paused requests
  are not metered."
  §6: define K to include pause- and configuration-induced cache rewrites.
  §7: "zero compression-attributed divergences among 64 Claude cache misses (Window B; more
  than 50,000 cache-write tokens; predecessor within five minutes). The 108-combination sweep
  used synthetic inputs."
Effect: §3, §6, §7, and the break-even interpretation for Claude.
Remaining uncertainty: How often pauses and configuration changes happened mid-conversation in
  the study windows.
```

```text
Finding: R-08
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: TS-C01
Severity: Moderate
Assessment: Needs qualification
Location: §2: "Let B_j and A_j denote the UTF-8 byte lengths of the original and actually
  forwarded request body j." Figure 3 caption (denominators). §8 (exact saved counts).
Problem: The meter's population is narrower than "request bodies", and by construction it
  differs from the §8 archive population.
  The meter counts only requests that meet all of these conditions:
  - a POST to /v1/messages, /responses or /chat/completions;
  - the saver was enabled and not paused;
  - the body was at most 10 MiB;
  - the response returned 2xx headers.
  Within that set, it counts zero-saving requests and every 2xx retry. Bodies are counted as
  received. Content-Encoding is not handled, so a compressed body would fail open and be
  counted at its compressed size.
  The exchange log behind §8 records every relayed request regardless of saver state. So
  1.41% against 3.45% differs by population definition, not only by window.
Evidence: LlmProxyRelay.cs:140-158 (measured only when a transform ran and the response was
  2xx); LlmProxySettingsService.cs:92-107 (a pause disables the saver);
  AnthropicBodyTransform.cs:22, 33-34 (the 10 MiB cap, sub-audit); LlmProxyRelay.cs:212-226
  (the exchange log is independent of the saver, sub-audit); there is no Content-Encoding
  handling in TokenSaver/ or VibeRails/ (sub-audit grep). Units are decimal: 343 / 9,930 =
  3.454%, which matches the reported 3.45%, while binary units would give 3.37%.
Proposed revision:
  §2, after the first sentence: "The meter records B_j and A_j only for POST requests to the
  provider inference paths whose TokenSaver was enabled and not paused, whose body was at most
  10 MiB, and whose response began with a 2xx status. Requests with no accepted rewrite are
  included with zero savings. Bodies are measured as received; the proxy does not decode
  Content-Encoding. The September archive in §8 records every relayed request and is a
  different population."
  Figure 3 caption: "GB and MB are decimal."
Effect: §2 definitions, Figures 3 and 7, and any comparison between them.
Remaining uncertainty: Whether any CLI sends compressed bodies, and how many requests were
  paused or over 10 MiB.
```

```text
Finding: R-09
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: TS-C01 (and README "Proposed web placement")
Severity: Moderate
Assessment: Needs qualification
Location: Figure 4 caption: "Dashed lines mark reported rollout dates, not randomized
  interventions." The rates in the abstract. README, "Proposed web placement".
Problem: The rates are unstable and concentrated, and most of the history in Figure 4 comes
  from a configuration the project later treated as a defect. The paper reports single rates
  without dispersion and does not flag this.
  (a) Codex is 1.41% for Sept 4–8 but 3.45% for Sept 4–Oct 1, in overlapping windows. Daily
      Codex rates since Sept 4 range from 0.00% to 12.14% (median 3.04%; unweighted mean 3.95%
      against a weighted 3.39%). The top five days hold 54% of Codex removal and 57% of Claude
      removal.
  (b) Across the 65-day series, 73% of all Codex bytes removed predate September 4. 61% fall in
      Aug 17–29 (14.9% byte-weighted), when truncation was "cutting holes in source files".
  (c) The public counter posts the lifetime total (ΣBytesBefore − ΣBytesAfter)/4 every 15
      minutes. It counts every re-send, and on this workstation it is dominated by the
      pre-mitigation period.
Evidence: daily-series.csv (recomputed); truncation_file_reads.md (title and §4–6);
  ITokenSavingsStore.cs:26; TokenSavingsPublishJob.cs:92-100 (sub-audit);
  Tests/Jobs/TokenSavingsPublishJobTests.cs:47, 63 (`totalTokensSaved`). The observed Codex
  drop is on Aug 30 (18.16% → 6.00%), but Figure 4 draws "file-read protection" on Aug 29.
Proposed revision:
  Figure 4 caption, add: "The August 17–29 Codex rates (15–28%) reflect truncation of file
  reads that the August 30 mitigation stopped; they do not represent the current pipeline."
  Move the line to August 30, or label it as the commit date.
  §4, add: "Rates vary widely between days (Codex 0.00–12.14% per day since September 4; five
  days hold 54% of the removed bytes) and between overlapping windows (Codex 1.41% for
  September 4–8), so a single rate is not a stable property of a provider."
  README: "Label the public counter 'estimated input tokens not transmitted (bytes ÷ 4,
  counted on every re-send)'. Do not describe it as billed or cost savings. Consider splitting
  it at the August 30 mitigation."
Effect: Figure 4, §4, abstract wording, the public counter text.
Remaining uncertainty: Per-conversation dispersion, which needs a database query.
```

```text
Finding: R-10
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: TS-C04 (and §1 populations)
Severity: Moderate
Assessment: Needs qualification
Location: §1: "Conversation keys are derived from opening messages, and the sequence extractor
  retains the request with the most tool calls per conversation. Branches and segments after
  compaction can be missed." §5 and Figure 5.
Problem: The keying causes more errors than the paper lists, and the "post-August 30"
  population contains pre-mitigation behaviour.
  (a) Collisions. Sessions with identical opening messages (repeated Automation or templated
      launch prompts, retries) share one key, and only the timeline with the most tool calls
      survives. Requests with no opening user message all share one "nokey" per provider.
  (b) Splits. The dossier found 3 of 51 Codex conversations split across 3–4 keys, covering
      622 of 1,000 requests.
  (c) Window. The filter uses the time a key was first seen, not the build or event time. One
      of the 35 Claude cases, and one of the three T0s, is the file-read investigation session
      itself: `cat TokenSaver/Shape/CommandShape.cs`, with 233 lines elided from a file of
      roughly 433 lines. The mitigation would have kept that file whole, so a pre-fix build
      processed it.
  (d) T0 notes. Two of the three Claude T0 notes begin with "Only you see that command's
      output…", which vibe-books' own trace extractor classifies as harness-inserted text. The
      acknowledgment match may come from that text.
Evidence: scan_conversations.py:27-28, 343, 443, 528, 661, 677-698;
  research_2026-10-01.md:523, 655-656; tiered report :25-29 (T0 examples);
  prompt-eval/extract_traces.py:66-67 (HARNESS_USER_PREFIXES); review_2026-10-01.md:285-286.
  Without the pre-fix case: 18/34 = 52.9%.
Proposed revision:
  §1: "Conversation keys hash the opening message, so identical openings collide (only the
  timeline with the most tool calls is kept), requests without an opening message share one
  key per provider, and some conversations split across keys (3 of 51 Codex conversations in
  one review)."
  §5: "The window is defined by when a key was first seen, not by the build in use. It
  includes one truncation by a pre-mitigation build (18/34 strict without it). Two of the
  three T0 notes begin with harness-inserted text, so T0 is unreliable."
  Suggested diagnostic: within a key, message count should never fall over time; a fall
  indicates a collision.
Effect: §1, §5, the T0 bar in Figure 5.
Remaining uncertainty: How often keys collide, which needs a query of mining_timeline.db.
```

```text
Finding: R-11
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: TS-C07
Severity: Moderate
Assessment: Needs qualification
Location: §7 table rows "Byte non-growth and untouched spans | Encoded-length gates in three
  rewriters; provider regression tests." and "Determinism and idempotence | Fixed
  configuration; cleanup flag combinations, condenser combinations and pipeline composition
  fixtures." Text: "The suite explicitly includes composition cases."
Problem: The table overstates what the 475 tests cover.
  - Whole-body byte-identity and adversarial non-growth tests (emoji re-escaping, lone
    surrogates) exist only for the Anthropic rewriter. The Codex and Chat Completions tests
    check substrings or parsed values.
  - The "composition" fixture runs Condense(Minify(x)), not CompressionPipeline.Run with the
    shipped plan. It has no shape stage and no file-read budget, and it turns ANSI stripping
    and CR collapse on, the opposite of the shipped defaults.
  - There are no property-based or fuzz tests.
  - Determinism is tested only as a repeat call in one process under one plan.
  - The run left out the Grok adapter tests (CliChatBodyTransformTests) and every meter and
    accounting test (Tests/DB/TokenSavingsStoreTests.cs, Tests/Jobs/
    TokenSavingsPublishJobTests.cs, Tests/Routes/LlmAnthropicProxyRoutesTests.cs). Every
    measured number depends on that accounting.
  The 475 count itself is right: 129 [Fact] and 47 [Theory] expand to exactly 475 cases. This
  is a source count; the tests were not re-run.
Evidence: Tests/TokenSaver/PipelineGoldenFixtureTests.cs:34-56 (MediumIds include CrCollapse
  and AnsiStrip, "No shape ids"; RunPipeline = Condense(Minify(...)));
  CompressionCatalog.cs:126, 134-137 (both stages off by default);
  CodexResponsesRewriterTests.cs:29-35 (Contains and parsed values);
  ChatCompletionsRewriterTests.cs:39-46 (parsed values); AnthropicMessagesRewriterTests.cs:191,
  211, 222 (adversarial cases, sub-audit); the validation.json filter.
Proposed revision:
  Table: "Byte non-growth and untouched spans | Encoded-byte gates in three rewriters (source
  inspection). Whole-body byte identity and adversarial escaping tests cover the Anthropic
  rewriter only; Codex and Chat Completions tests check parsed values."
  "Determinism and idempotence | Repeat calls in one process under one fixed plan. The
  composition fixture is Condense(Minify(x)) with non-default ANSI and CR flags;
  CompressionPipeline.Run with the shipped plan is not tested for idempotence."
  Text: replace "The suite explicitly includes composition cases" with "The suite includes a
  two-stage composition fixture, not the shipped pipeline." List the excluded test classes in
  the scope note.
Effect: §7 table and text, and the abstract's "475 focused tests passed".
Remaining uncertainty: None about scope. Property tests and full-pipeline idempotence tests
  would have to be written.
```

```text
Finding: R-12
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: TS-C08
Severity: Moderate
Assessment: Needs qualification
Location: §8: "The saved flags show zero character-growth, canonical-JSON-byte-growth,
  nondeterminism or idempotence failures, and the completed run reports no exceptions."
Problem: The replay never compressed a raw output that had been compressed live. Its inputs
  were recorded after-texts, so "zero growth" means the pipeline did not grow text it had
  already forwarded, which is close to a fixed-point check. "Deterministic" means a second
  call in the same process.
  The paper also omits two results:
  - 45 allowlisted after-texts were not fixed points of that commit's defaults: 44 changed
    only whitespace, and one gained a new truncation of 5,977 characters.
  - 28 raw outputs had more than one recorded forwarded version.
  Both bear on the cache stability the economics assume (R-07).
Evidence: replay_pipeline/Program.cs:23 (`string raw = root.GetProperty("text")`, which holds
  the exported After column) and :41 (the second call); report_recent.py:19-20 (`SELECT
  Hash,Tool,Command,After`, sub-audit); findings.md:25-29 and 157-159. A recount of
  replay-output.jsonl gives 8,177 records, 4 configurations and zero flags (sub-audit recount).
Proposed revision: "The replay re-ran the pipeline on the recorded forwarded texts
  (after-texts), not on the original raw outputs, so it tests non-growth and stability of
  already-compressed text rather than the raw-to-compressed step. Determinism was checked by a
  second call in the same process. Forty-five allowlisted after-texts were not fixed points of
  the defaults at that commit (44 whitespace changes and one new truncation), and 28 raw
  outputs had more than one recorded forwarded version. These were attributed to pauses, older
  builds or wire-guard decisions but not resolved."
Effect: §8 paragraphs 2–3.
Remaining uncertainty: Replaying raw inputs needs the raw column, which means a database
  query.
```

```text
Finding: R-13
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: TS-C09
Severity: Moderate
Assessment: Needs qualification
Location: §9: "Offline replay of the production predicate over 261 previously elided outputs
  detected 205 for protection (78.54%)". Figure 8 caption, "mitigation coverage".
Problem: 205/261 is the detector's positive rate on elided outputs of every kind. With no
  ground-truth labels it is neither recall nor precision, and the predicate is deliberately
  permissive. The detector also has concrete misses:
  - `awk` is not in its executable list, so a file read such as
    `awk '{printf "%d\t%s\n", NR, $0}' file` gets the ordinary 150/50 cut. The post-August 30
    window contains three truncated Claude awk file reads, each followed by a strict (T2)
    related call. The two printed in the report are line-range re-reads.
  - Diffs are excluded by design.
  - Recognised reads over 262,144 UTF-16 units fall back to 150/50, so the largest files are
    cut hardest.
  The deployment time is not recorded either.
Evidence: truncation_file_reads.md:104-112 (permissive by design), 180-181 and 193 (the
  sample is all unique elided outputs, plus later traffic); Shape/CommandShape.cs:196-199
  (FileDumpCommands = cat, bat, nl, head, tail, more, less, type, get-content, gc, sed) and
  :268-271 ("Known gap … git show/git diff payloads … still truncate").
  Tiered report :17: "other: awk | 3 | … | 3 (T2) | … | 100%". The T2 example `awk '{printf
  …}' Tests/Services/Sess… raw=27032 after=8875 elided=404` is followed by
  `awk 'NR>=150 && NR<=330' …`.
  OutputCondenser.cs:166-170 and FileReadTruncationTests.cs:186-196 (the fallback).
Proposed revision: "The 261 outputs were all unique elided outputs from August 26–29, not a
  labelled set of file reads; 205 (78.54%) satisfy the deliberately permissive predicate. This
  is the predicate's positive rate, not its recall or precision. Known misses remain: awk-based
  file reads are not recognised (three truncated Claude cases after August 30, each followed by
  a related call), diffs are excluded by design, and recognised reads over 262,144 UTF-16 units fall
  back to the 150/50 budget."
Effect: §9, Figure 8 caption, the §1 table row.
Remaining uncertainty: Recall needs a labelled sample.
```

```text
Finding: R-14
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: §1 (supports TS-C02 and TS-C09; affects the §12 arms)
Severity: Moderate
Assessment: Needs qualification
Location: §1 mechanism table; "System instructions, user messages and tool-call inputs are
  outside the selected tool-result spans."
Problem: The mechanism table leaves out stages and limits that decide what is removed.
  (a) "Collapse passing tests", a lossy stage that is on by default, replaces runs of passing
      test lines with `[... N passed ...]`. Three reshaping stages are also on by default.
  (b) Dedupe, truncation and shape filters skip any output containing ESC, BEL or CR, and ANSI
      stripping is off by default, so coloured or progress-bar output is never truncated.
  (c) The default allowlist includes Codex `exec` (whose output can contain other tools'
      results) and Grok `get_command_or_subagent_output`.
  (d) In the Anthropic API, tool_result blocks sit inside role:user messages, and any client
      text inside a tool_result is rewritten as if it were output. Error results are treated
      the same as successes.
  (e) Claude Code already caps Bash output at 30,000 characters upstream, so Claude truncation
      only trims outputs of 4–30 KB.
  Several line citations have also drifted.
Evidence: CompressionCatalog.cs:150-186 (Reshaping stages at order 7–9 and the Lossy "Collapse
  passing tests" at order 10, all OnByDefault: true), :134-137 (ANSI off), :201-223
  (allowlists), :231 and :241 (Read and Grep off by default); OutputCondenser.cs:144-148
  (whole-string abort on '\x1b', '\a', '\r'); research_2026-10-01.md:511-512 (the 30K cap);
  tiered report :23 ("largest raw shell result seen … 29934").
  Citation drift:
  - OutputCondenser.cs:55 is blank; the constants are at :54 and :56-57.
  - The file-read fallback is at :166-170.
  - CompressionCatalog.cs:201 is the Shell scope.
  - The /4 estimate is at ITokenSavingsStore.cs:26.
Proposed revision:
  Add rows:
  - "Passing-test collapse | Lossy, on by default: runs of passing-test lines from recognised
    test runners become [... N passed ...]."
  - "Control characters | Lossy stages skip any output containing ESC, BEL or CR; ANSI
    stripping is off by default."
  - "Upstream caps | Claude Code limits Bash output to 30,000 characters before TokenSaver
    sees it."
  Eligibility row, add: "Codex exec and Grok sub-agent output are allowlisted; client text
  inside an eligible result is rewritten with it."
  Replace the scope sentence with "System prompts, user-typed text blocks and tool-call inputs
  are not rewritten; in the Anthropic API, tool results are blocks inside user-role messages."
  Update the line numbers in references [3]–[5].
Effect: §1, references, the §12 arm definitions.
Remaining uncertainty: None.
```

```text
Finding: R-18
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: TS-C05
Severity: Moderate
Assessment: Needs qualification
Location: §6, equation 4 and Figure 6.
Problem: The break-even model covers only one of the costs.
  (a) The scope of G is undefined. Recovery risk applies only to lossy removal, so G must be
      measured per lossy event, not as the meter total.
  (b) K is described only as a cost, but it can be negative: fewer or later compactions,
      repricing on cache expiry (R-17), and fewer context-limit failures.
  (c) There is no term for silent quality loss, where the agent proceeds without the omitted
      content and produces worse work. That is plausibly the largest cost.
  (d) Figure 6's C/G ∈ {1, 2, 4} is unanchored. The paper's own aggregates put a Claude
      recovery that adds a round trip at C/G ≈ 2.1–2.7 (R-03).
Evidence: Equation 4 as written; R-03 arithmetic.
Proposed revision: "E[N] = G − qC − rF − K, where G is the gross avoided cost of lossy removal
  events only, r is the probability that compression causally degrades the task outcome
  without a recovery, F is the cost of that degradation, and K (which may be negative)
  collects cache, compaction and context-limit effects." On Figure 6, mark the C/G range
  implied by one extra round trip at the September audit's mean context, labelled as an
  assumption.
Effect: §6, Figure 6.
Remaining uncertainty: q, r and F are unmeasured.
```

## Minor findings

```text
Finding: R-15
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: TS-C02
Severity: Minor
Assessment: Needs qualification (the proposition holds)
Location: Abstract: "A source-level proof establishes byte non-growth for accepted
  replacements". The §2 proof and scope paragraph.
Problem: Proposition 1 holds exactly as stated. The three rewriters copy untouched bytes
  verbatim and accept a candidate only when its encoded byte count is strictly smaller. Every
  exception path forwards the original bytes. But "source-level proof" overstates it: the
  proof is of the splice model, matched to the source by inspection, and there is no
  whole-body runtime assertion.
  Two scope notes are missing:
  - Inside an accepted span, unchanged characters get .NET's escaping (non-BMP characters
    become 12-byte \u pairs, and client escapes are decoded). The gate absorbs this. The code
    comment claiming parity with JSON.stringify (AnthropicMessagesRewriter.cs:44-45) is wrong
    for non-BMP characters.
  - Byte non-growth does not imply cost non-growth: a smaller request that breaks the cached
    prefix costs more (R-07).
  The proof also indexes both span kinds by i, but n eligible spans have n + 1 untouched spans
  v_0 … v_n.
Evidence: AnthropicMessagesRewriter.cs:149-151 and 228-233 (verbatim copy), :188 (gate);
  CodexResponsesRewriter.cs:209; ChatCompletionsRewriter.cs:176;
  AnthropicMessagesRewriterTests.cs:222 (an emoji re-escape rejected by the gate, sub-audit).
Proposed revision:
  Abstract: "A proof under the splice model, matched to the three rewriters by source
  inspection, establishes that an accepted replacement cannot enlarge the request body."
  §2 scope paragraph, add: "Nor does it imply lower cost: a smaller request whose bytes differ
  from an earlier cached prefix can cost more than the original."
Effect: Abstract wording; §2.
Remaining uncertainty: None.
```

```text
Finding: R-16
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: TS-C06
Severity: Minor
Assessment: Needs qualification (the proposition and counterexample hold)
Location: §7, "Proposition 2. Exact task preservation has a necessary and sufficient condition"
  and the counterexample.
Problem: The proposition is the standard factorisation lemma and is correct. The
  counterexample is also valid: the marker carries only the omitted line count
  (`[... N lines elided ...]`), so equal line counts are enough.
  But the condition needs the task function g on every input, and the decoder h may be
  arbitrary. It is therefore neither testable on traffic nor informative about what an LLM can
  extract. Agents can also re-run commands, so in practice preservation is a property of the
  interactive policy at some cost (§6, §12), not of a static decoder.
  Equation 5 also omits the quantifier on the right-hand side.
Evidence: OutputCondenser.cs:90-91 and 392-399 (the marker format).
Proposed revision:
  Heading: "Proposition 2. No lossy compressor preserves every task".
  After the counterexample, add: "The condition cannot be tested in practice because it
  requires the task function on every input, and the decoder it guarantees may be arbitrarily
  complex. For an agent that can re-run commands, the question that matters is whether its
  task outcome and cost change, which only the §12 experiment can measure."
  Equation 5, right-hand side: "∀x, y: C(x) = C(y) ⇒ g(x) = g(y)".
Effect: §7.
Remaining uncertainty: None.
```

```text
Finding: R-17
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: TS-C03
Severity: Minor
Assessment: Needs qualification
Location: The §3 cache identity; Figure 2 caption, "Illustrative accounting with w = 1.25 and
  fixed cache-read multipliers"; the §6 worked example.
Problem: The illustrative parameters do not match the providers studied, although the paper's
  own data could anchor them.
  (a) α = 0.05 and α = 0.025 correspond to no provider in the study.
  (b) w = 1.25 covers only Anthropic's 5-minute writes. In §10, 46.7% of Claude's write tokens
      are one-hour writes at 2×, so the write-weighted w̄ is about 1.60.
  (c) Most of the removed bytes come from Codex, and OpenAI has no write premium (w = 1) and
      caches automatically on a best-effort basis. Cached-token usage exists for 1,743 Codex
      responses but is not reported.
  (d) The identity assumes every later appearance is a cache hit. Cache expiries rewrite the
      prefix at w, which makes removal more valuable. The dossier attributes 58 large Claude
      misses (13.6M tokens) to one-hour TTL expiry.
  (e) The September audit's 28.8 cache-read tokens per written token is a usable anchor for m.
Evidence: §10 table (8,733,135 / 18,718,783 = 46.7%); research_2026-10-01.md:583-590 (miss
  causes); research-discovery.txt (100 of 1,843 Codex usage records missing).
Proposed revision:
  Figure 2: plot w ∈ {1.0, 1.25, 2.0} at α = 0.1 instead of α ∈ {0.1, 0.05, 0.025}.
  §3, add: "Provider parameters differ. Anthropic charges 1.25× for 5-minute writes and 2× for
  one-hour writes (46.7% of Claude write tokens in §10 were one-hour), and OpenAI has no write
  premium. A cache expiry rewrites the prefix at the write price, so the identity understates
  gross avoided cost in sessions with long idle gaps."
Effect: Figure 2, §3, the worked example.
Remaining uncertainty: The Codex cache-hit share, which needs the usage records.
```

```text
Finding: R-19 (presentation)
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: —
Severity: Minor
Assessment: Presentation
Problems and proposed revisions:
  1. The figure files are numbered out of order: Figure 1 is figures/03-resend-amplification,
     Figure 2 is 04-cache-weighting, Figure 3 is 01-observed-wire, and Figure 4 is
     02-daily-series. Renumber the files to match the figures.
  2. The symbol C means four different things: the compressor (§7), the recovery cost (§6),
     an experimental arm, and the task cost C_{k,C} (§12). Use distinct symbols.
  3. Percentages to two decimals on n = 35 imply false precision. Report counts with an
     interval (Wilson 95% for 19/35 is 38–70%) or use one decimal place.
  4. The abstract does not state the meter window. Add "(activity September 4 to October 1)".
     For comparison, the 65-day rounded totals are 0.38% for Claude and 5.86% for Codex.
  5. The workstation's billing regime (API keys or subscription) is not stated. Under a flat
     subscription, the outcome that matters is quota consumption, not billed tokens.
```

```text
Finding: R-20
Reviewer: Claude (claude-opus-5-5)
Baseline: TokenSaver paper 0.2
Claim ID: §13
Severity: Minor
Assessment: Needs qualification
Location: §13 Interpretation.
Problem: The interpretation lacks scale. In the same September window, tool definitions that
  were never called occupied 347.5M characters across requests, about 89 times the
  compressor's 3.89 MB of Claude removal. A single MCP server disconnect cost about 60% of the
  compressor's entire Window B gross saving on Claude.
Evidence: codex-mine-0908/findings.md:42-45; research_2026-10-01.md:592-597.
Proposed revision: §13, add: "For scale: in the September 4–8 window, definitions of tools
  that were never called occupied about 89 times as many characters as TokenSaver removed from
  Claude requests, and a single MCP disconnect cost about 60% of the compressor's gross Claude
  saving in the dossier's Window B."
Effect: §13.
Remaining uncertainty: The tool-definition figure is an opportunity ceiling, not a removable
  amount.
```

---

## Checks that passed (no change needed)

- **Arithmetic.** I recomputed every ratio and percentage in §4, §5, §8, §9 and §10, the worked
  example (3,150; 0.315; +2,150; −1,850) and Figure 2's 5.15. All are exact. The daily totals
  reproduce 0.192% and 3.394%. The tier counts sum to their denominators, and the 31.26–35.33%
  bound is correct.
- **Units.** The snapshot sizes are decimal, and the reported percentages match decimal
  arithmetic.
- **Formats.** Every number of three or more characters is identical across the Markdown, PDF
  and Word copies. Word Track Changes is on. The Markdown and Word hashes match
  `review-baseline.json`.
- **Provenance.** All 18 manifest SHA-256 hashes match.
  - The TokenSaver code is unchanged from `336373c0` to HEAD `f9e43608`. The only difference is
    an uncommitted four-line link to this paper in `TokenSaver/README.md`.
  - The vibe-books sources are unchanged at HEAD.
- **Proposition 1.** The rewriters are a true splice with a strict encoded-byte gate at the cited
  lines, and every exception path forwards the original bytes.
- **475 tests.** A count of the source matches exactly. The tests were not re-run.
- **§1 thresholds.** All values are correct: 3, 150/50, 1,200/200, 262,144, 10 lines and 4,096
  units.
- **Token estimate.** The /4 is applied to aggregate sums, not floored per request
  (ITokenSavingsStore.cs:26). The paper's "floor(BytesSaved / 4)" is correct in effect.
- **Replay flags.** A recount gives zero growth, zero nondeterminism and zero non-idempotence
  (sub-audit).

## Classification

- **Factual corrections:**
  - R-05 (definition and units)
  - R-06 (population)
  - R-08 (meter population)
  - R-10 (pre-fix case and keys)
  - R-11 (test scope)
  - R-12 (replay omissions)
  - R-13 (positive rate; the awk miss)
  - R-14 (omitted stages; citations)
  - the stated rationale in R-03
- **Interpretation:** R-01, R-02, R-03, R-04, R-07, R-09, R-16, R-17, R-18, R-20.
- **Presentation:** R-15, R-19.

## Conflicting evidence, reported rather than resolved

- The Claude byte rate is diluted by incompressible classifier traffic, which favours
  TokenSaver: main traffic is at least 0.295% against 0.229% (R-06). Claude's gross saving is
  also small enough that a cache break or a modest causal recovery rate erases it, which
  disfavours TokenSaver (R-03, R-07).
- Cache expiry makes removal more valuable than the paper's identity allows (R-17), and the
  follow-up rates may be largely base-rate behaviour (R-02). Both favour TokenSaver.
- The dossier's sensitivity analyses point opposite ways by provider: Claude negative, Codex
  positive (R-03).

## Sources

**Inspected.**
- Package:
  - the manuscript in all formats;
  - `evidence.json`, `daily-series.csv`, `source-manifest.json`, `validation.json`,
    `research-discovery.txt`;
  - figures 02, 03, 04, 05, 06 and 09.
- Product:
  - `TokenSaver/Minify/*` (the three rewriters, OutputCondenser, ToolOutputRewriteResult,
    AnthropicBodyTransform);
  - `TokenSaver/Pipeline/CompressionCatalog.cs`, `TokenSaver/Shape/CommandShape.cs`,
    `TokenSaver/LlmProxyRelay.cs`, `TokenSaver/LlmAnthropicProxyRoutes.cs`,
    `TokenSaver/README.md`;
  - `VibeRails/Services/LlmProxy/LlmProxySettingsService.cs`,
    `VibeRails.Data.Abstractions/DB/ITokenSavingsStore.cs`;
  - `Tests/TokenSaver/*` and `Tests/Jobs/TokenSavingsPublishJobTests.cs`.
- vibe-books:
  - `token_saver/research_2026-10-01.md`, `review_2026-10-01.md`,
    `truncation_file_reads.md`, `v1_findings_claude_codex_response.md`
    (via sub-audit);
  - `python-scripts/token_saver/tiered_rerun_audit.py`, `scan_conversations.py`;
  - `results/vibe6-paper/tiered_rerun_…201359.md`;
  - `results/codex-mine-0908/findings.md`, `summary.json`, `replay-output.jsonl`
    (via sub-audit);
  - `replay_pipeline/Program.cs` (via sub-audit);
  - `vibe-data/docs/backup-coverage-audit.md`;
  - `prompt-eval/extract_traces.py`.

**Unavailable or deliberately not opened.**
- `proxy_exchanges.db`, `mining_timeline.db`, `codex-mine-0908/recent.db` and `state.db`
  (the review policy forbids database queries).
- Study A's computation scripts, which were not preserved.
- The full 300-character T0 notes; only excerpts are saved.

## Cheapest next steps

1. **Base rate (R-02).** Run the existing classifier over Codex Aug 1–16, over untruncated large
   outputs, and just below the activation threshold. This is read-only on existing captures.
2. **Classifier-free recount (R-06).** Recount the September audit by day and without the
   classifier family.
3. **Exact gross token deltas (R-04).** Count tokens for stored original/forwarded body pairs.
   Anthropic's counting endpoint is free.
4. **Claude truncation-off before/after.** This is the dossier's §13.1 experiment and an owner
   decision; it is a product change, not part of this paper.
5. **Fork-at-truncation pilot (R-04).** A small run on recorded truncation events, to measure
   between-arm variance before sizing any complete-task study.
