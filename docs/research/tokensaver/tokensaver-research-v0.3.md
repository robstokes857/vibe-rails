# Measuring TokenSaver Compression Savings

Byte-level guarantees, cache-aware economics, and observed agent behavior

RESEARCH DRAFT 0.3  |  5 OCTOBER 2026  |  Prepared for Robert Stokes

Reconciled by Codex after checking Claude’s 20 review findings. The unchanged 0.2 baseline is `tokensaver-research-review.md`; see `review-notes-codex.md` and `CHANGELOG.md`. This revision is Markdown only; the Word, PDF, LaTeX and ZIP exports remain explicitly version 0.2.

### Abstract

TokenSaver rewrites selected tool-result strings before coding-agent requests reach a model provider. This paper examines whether those rewrites constitute useful savings. It combines a 65-day dossier, additional archived studies, a current source audit, nine figures, and formal accounting and correctness arguments. For activity September 4 through the first October 1 meter snapshot, accepted rewrites mechanically removed **3.45% of request-body bytes for Codex CLI**, **0.20% for Claude Code**, and **0.18% for Grok CLI**. These compare forwarded bodies with the bodies submitted by the CLI on the observed trajectory. They do not measure total traffic or billing relative to a run without compression. [1]

Two historical ratios previously described as replay multipliers combine meter bytes with deduplicated decoded characters; they cannot identify replay counts. A heuristic classifier finds a related call in the next assistant turn after 19/35 Claude truncations and 146/448 classified Codex truncations. No comparable untruncated group was classified, so these frequencies do not establish changed behavior. A proof under a splice model, checked against the three rewriters by source inspection, establishes conditional byte non-growth. A recorded run of 475 focused tests passed on October 5; this review did not rerun it. A second proof characterizes exactly when a compressed observation can preserve a task answer; arbitrary truncation cannot satisfy that condition for every task.

The evidence establishes gross transport reduction and specific engineering guarantees. It does not yet establish net billed-token savings or noninferior task success. We derive conditional break-even equations and propose a paired experiment whose detectable cost effect and quality margin must be sized from pilot data, without converting character counts or heuristic follow-ups into token claims.

| Claim | Evidence status |
| --- | --- |
| Forwarded body smaller than the submitted body | Measured per request; net workload effect unmeasured |
| Accepted rewrite cannot enlarge the body | Proved under the splice model; source checked |
| Stable rewriting and protocol behavior | Supported by focused regression tests |
| Lower total task cost with equal quality | Open empirical question; protocol specified |

The preserved revision 0.2 added a bounded September audit of 8,286 successful requests, a historical C# replay of 8,177 output variants under four configurations, actual usage buckets, and the file-read mitigation study. Historical replay evidence is kept separate from the 475 tests recorded for revision 0.2.

## 1. System and study design

TokenSaver is a local proxy with provider-specific JSON rewriters. It correlates tool calls with their results, selects eligible text, runs deterministic cleanup, recognized-command reshaping and condensation stages, and accepts a replacement only if its serialized byte representation is smaller. The studied design passes provider responses through unchanged. System instructions, ordinary user text blocks and tool-call inputs are outside the selected tool-result spans. Anthropic tool results reside inside user-role messages; eligible result text, including client-added text or error text inside those results, can be rewritten. [1, 3]

| Mechanism | Current implementation boundary |
| --- | --- |
| Eligibility | Allowlisted shell/background results, including Codex `exec` and Grok `get_command_or_subagent_output`; dedicated Read/Grep scopes default off. Shell results can contain source files. |
| Cleanup | Configured line-ending, trailing-space and blank-line normalization; not universal semantic equivalence. |
| Passing-test collapse | Default-on lossy `elide-passed-tests` replaces runs of passing-test lines from recognized runners with a count marker. Three command-grouping stages also default on. |
| Control characters | Shape and condensation stages decline text still containing ESC, BEL or CR after cleanup. Default CRLF normalization removes CRLF; ANSI stripping and progress-redraw collapse default off. |
| Upstream limits | Historical sources report a Claude Code Bash cap near 30,000 characters, with 29,934 observed in the tiered window. This is a historical harness observation, not a universal CLI guarantee. |
| Run collapse | At least three identical consecutive lines; replace only when the marker is smaller. |
| Truncation | Normally retain 150 head and 50 tail lines plus an elision marker. |
| File-read mitigation | Recognized file reads use 1,200 head and 200 tail lines if the post-dedupe text has at most 262,144 UTF-16 code units. |
| Activation threshold | Omitted middle must contain at least 10 lines and 4,096 UTF-16 code units. This is not a token or byte cap. |

### Populations and time windows

The dossier covers one developer's workstation from July 29 through October 1, 2026. Its derived sequence dataset contains 65,582 requests and 4,015 conversation keys; a separate historical corpus contains 49,851 content-hash-distinct tool outputs. These populations are not interchangeable. The sequence extractor hashes the first message for Anthropic, the first two user messages for Responses, and the first user message for Chat Completions. Identical key inputs collide; missing key material shares a provider-specific `nokey`. It retains the request with the most tool calls, breaking ties by message count. Branches and post-compaction segments can be missed; an earlier review found three of 51 provider-key conversations split across multiple scanner keys. These are extractor limitations, not measured collision rates. The bounded September miner in section 8 additionally prefers a provider cache key when available. [1, sections 3-5 and 7.8; 2, 9]

The primary meter table uses activity from September 4 through the first October 1 snapshot. The daily figure uses a later October 1 reread. The follow-up classifier uses conversations first observed on or after August 30. Study A's distinct-content comparison uses August 28 for Claude and August 28-29 for Codex. Each figure states its own denominator and window.

Current source audit: vibe-rails 336373c0c51b2dc68abb3350910cee533502651a. Historical research checkout: vibe-books cf3253b99e6992e7b62564bfc3fb23f276a0e39c. File hashes accompany the draft. No raw request bodies were copied into the paper.

## 2. Accounting and a transport guarantee

Let $B_j$ and $A_j$ denote the UTF-8 byte lengths of the original and actually forwarded request body j. For the current implementation, the meter includes only qualifying JSON POSTs with a detectable body to inference paths (`/v1/messages`, `/responses`, `/chat/completions`), while compression is enabled and not paused, within the 10 MiB buffer cap, and after upstream 2xx response headers. Zero-saving requests count; successful retries count separately; later stream failure is not excluded by that header check. The proxy does not decompress Content-Encoding before the rewrite. The exchange archive has a broader logging path and section 8 applies its own HTTP-200 filter; current source inspection alone cannot certify every historical build’s filters.

The gross mechanical reduction is the sum of accepted body differences. The byte-weighted share uses the sum of original bytes as its denominator; an unweighted mean of daily percentages estimates a different quantity.

$$
D_B=\sum_{j=1}^{J}(B_j-A_j),\qquad S_B=\frac{D_B}{\sum_{j=1}^{J}B_j}\tag{1}
$$

For a fixed provider model m, let $\tau_m(x)$ be its input-token counting function. A paired request-level token difference requires counting both complete original and transformed requests with that same model. A byte reduction does not imply a token reduction: tokenization is not monotone in string byte length, and model-specific framing also matters. The TokenSaver savings field instead uses floor(BytesSaved / 4), an explicit estimate. [4, 7]

$$
D_T=\sum_{j=1}^{J}\left[\tau_m(B_j^{\rm body})-\tau_m(A_j^{\rm body})\right]\tag{2}
$$

### Proposition 1. Accepted splices cannot enlarge the body

Assume the parser identifies n disjoint eligible JSON string spans $s_1,\ldots,s_n$ and n + 1 untouched spans $v_0,\ldots,v_n$. Let $c_i$ be an encoded candidate replacement, and let $a_i$ equal $c_i$ only when its UTF-8 byte length is strictly smaller than that of $s_i$; otherwise $a_i$ equals $s_i$. The rewrite copies each $v_i$ without modification. Then:

$$
|R(B)|=\sum_{i=0}^{n}|v_i|+\sum_{i=1}^{n}|a_i|\ \leq\ \sum_{i=0}^{n}|v_i|+\sum_{i=1}^{n}|s_i|=|B|\tag{3}
$$

**Proof.** Every accepted span satisfies |$a_i$| < |$s_i$|, every rejected span satisfies equality, and disjoint concatenated byte lengths add. At least one accepted replacement makes the total inequality strict. All bytes outside accepted spans retain their original values. The three provider rewriters enforce the encoded-length comparison at the source locations listed in reference 3.

This is a guarantee about the implemented splice contract, conditional on correct parsing and copying. It does not prove that every tool-result meaning is preserved, that tokenization shrinks, or that a complete task requires fewer requests. Encoding inside accepted spans can change escapes; the strict encoded-byte gate absorbs that expansion. Smaller bytes can still cost more if a cacheable prefix changes. Candidate stage traces can include rejected rewrites; actual savings must be conditioned on RewriteAccepted or derived from the forwarded body. This is a model proof supported by source inspection, not a formal verification of the parser or adapter.

## 3. Distinct content, replay, and cache weighting

For a hypothetical collection of distinct removed blocks measured in one consistent unit, let d_i be removal per block and m_i the total number of request appearances, including the first. If rewriting is stable, sum(m_i d_i) / sum(d_i) is a removal-weighted appearance count. The historical Study A aggregates do **not** establish that quantity.

Study A’s original report calls its numerator the whole-request meter tally, which measures bytes, while its denominator is decoded characters removed from content-hash-distinct outputs. The later dossier relabels the numerator as characters. The reported pairs are 3,337,920 / 264,898 for Claude on August 28 and 51,728,364 / 1,299,384 for Codex on August 28–29: numerically 12.60 and 39.81 bytes per deduplicated character under the original meter interpretation. Replay, identical content across conversations, JSON escaping and encoding all affect the comparison. These pre-mitigation samples cannot supply m for a token-cost model. The scratch computation was not preserved, so exact historical provenance remains unresolved; this revision conservatively retains the reported quantities with separate units. [1, section 6.3; 10, sections 1 and 7]

![Figure 1](figures/v0.3/01-study-a-units.png)

Figure 1. Historical Study A, before the file-read mitigation: reported meter tally (bytes, following the original report) and deduplicated removed content (decoded characters). Each panel has its own unit. The numeric quotients are not dimensionless replay multipliers.

Repeated input can be charged at a cache-read rate. For a block of d actual tokens, first-write multiplier w, read multiplier alpha, and m appearances, its gross avoided input cost is d[w + alpha(m - 1)] in units of the base input price. This identity assumes unchanged future requests, token counts and cache boundaries, exactly one initial write and m - 1 cache reads, with no expiry, eviction or bypass. More generally, count each appearance at its actual applicable rate. It is a conditional accounting model, not an observed net effect.

![Figure 2](figures/04-cache-weighting.png)

Figure 2. Illustrative accounting with w = 1.25 and fixed cache-read multipliers. At m = 40 and alpha = 0.10, the cost multiplier is 5.15, versus 40 raw token appearances. These curves are hypothetical parameter sensitivities, not provider price quotes or a fit to study traffic.

Stable rewriting requires fixed input, command metadata and configuration. Settings and pause state are resolved per request. A pause, resume or changed setting can alter previously sent result bytes and invalidate the affected cached suffix; two transition opportunities are not two guaranteed full-prefix misses. No cost follows if relevant bytes do not change, no cache exists, or a matching prefix remains reusable. Paused requests are outside the meter. [3, 4]

Provider documentation cited by the baseline distinguishes cache-write and cache-read prices and includes model-specific exceptions. Rates must be pinned per model and date before calculating money. The study's historical provider-wide factors are not used here to claim current dollar savings. [6]

## 4. Measured request-body reduction

![Figure 3](figures/01-observed-wire.png)

Figure 3. Reported gross request-body byte reduction in the first October 1 meter snapshot, activity since September 4. Denominators: Claude 16,238 requests; Codex 16,227; Grok 1,212. GB and MB are decimal. Sizes in the dossier are rounded; percentages are reproduced as reported. [1, section 6.1]

| Agent | Original body data | Removed | Share |
| --- | --- | --- | --- |
| Claude Code | 7.71 GB | 15.4 MB | 0.20% |
| Codex CLI | 9.93 GB | 343 MB | 3.45% |
| Grok CLI | 673 MB | 1.2 MB | 0.18% |

![Figure 4](figures/v0.3/04-daily-series.png)

Figure 4. Daily reported byte-reduction percentages from the later October 1 meter reread. Missing Claude days remain gaps; October 1 is partial. Dashed lines mark the historical allowlist annotation and the August 30 file-read mitigation commit (2819e6ba), not verified deployment times or randomized interventions. Axis scales differ by panel. [1, Appendix A.1]

The later daily ledger gives approximately 0.19% for Claude and 3.39% for Codex when rounded daily byte totals are summed before division. Those later totals are not combined with the earlier Figure 3 counts. Codex daily reported rates since September 4 span 0.00–12.14% (median 3.04%); its five highest-removal days account for 53.6% of removal, compared with 57.1% for Claude. Across the full series, 60.7% of Codex removal falls on August 17–29, before the mitigation commit. That period’s daily rates span 0.94–28.21%, not a stable 15–28%. A commit date is not a measured deployment boundary. Provider mix, harness behavior, meter filters and workload changed over time; rates are not intrinsic provider properties and these annotations alone do not identify causal savings.

## 5. What agents do after truncation

The corrected audit assigns the strongest related-call tier found in the next assistant turn. T0 requires acknowledgment-regex text and a related call; T1 requires an exact stored-command repeat. T2 includes a Read sharing a basename, a pause call, two shared file-like basenames, or one such basename with range-like text; T3 retains a looser relation. T1 is zero in the plotted groups. All 146 strict Codex cases are T2, with zero T0/T1. Ordinary next steps after a diff can meet T2. No comparable untruncated base rate was measured, so these are conditional classifier frequencies, not an estimated association or causal extra-turn rate. [1, section 7.2; 2]

![Figure 5](figures/05-followup-tiers.png)

Figure 5. Next-turn related-call classification after truncation. Codex excludes 19 of 467 unresolved command wrappers. Diff/show/log and grep are subsets of the Codex classified group; they are not additional independent populations.

| Population | Strict T0-T2 | Including loose T3 |
| --- | --- | --- |
| Claude, all truncations | 19 / 35 = 54.29% | 26 / 35 = 74.29% |
| Codex, classified | 146 / 448 = 32.59% | 199 / 448 = 44.42% |
| Codex, diff/show/log | 122 / 284 = 42.96% | 156 / 284 = 54.93% |
| Codex, grep | 14 / 106 = 13.21% | 28 / 106 = 26.42% |

Treating all 19 unresolved Codex cases as negative or positive bounds the full-sample strict fraction between 146/467 = **31.26%** and 165/467 = **35.33%**. This is a sensitivity bound for missing classifications only. It does not cover classifier error, missed later recovery, missing conversation branches, or causality.

The audit also reports 318 thousand removed characters and 152 thousand follow-up-output characters for Claude; Codex reports 8,255 thousand and 1,860 thousand. These are decoded-character sums over marked result instances in retained timelines, before replay weighting. They are not guaranteed globally distinct content. The follow-up may contain different information, and may have occurred anyway. Subtracting these columns would not measure net savings.

The window filters first-seen keys, not executing builds. One saved Claude case truncates a small `cat` file read in a way inconsistent with today’s widened budget; it suggests older behavior but does not identify a build. Omitting this case gives 18/34 (52.9%) as a sensitivity calculation, not a corrected current-build estimate. Two T0 examples begin with harness text and report an acknowledgment match outside the displayed excerpt; the complete 300-character notes were not inspected, so authorship and validity of those matches remain unresolved. Counts are retained as recorded. [2]

A zero-rewrite Codex meter period (August 1–16, 6,589 requests) is a candidate for a future control audit; it is not proof of absent earlier compression in retained histories or of comparable tasks. Threshold-adjacent controls likewise require valid continuity and selection assumptions.

No population confidence intervals are attached to these counts: result instances cluster within conversations, the workload comes from one developer, and the classifier itself is imperfect. Silence after truncation is not evidence that the omitted content was irrelevant.

## 6. The economics of recovery

For a prespecified unit such as a task or one intervention and its continuation, let G be gross avoided input cost under a fixed baseline continuation, q the probability that compression causally adds recovery, H the expected incremental recovery cost conditional on that event (including all recovery episodes within the unit), and K other incremental monetary effects. K may be negative and includes cache, compaction and context-limit effects. Define the terms without overlap; if G varies, use its expectation. Recovery and other changes must be compared with the same baseline. All costs use the same currency or a fixed base-price unit. Under this explicitly simplified model:

$$
E[N]=G-qH-K,\qquad q^*=\frac{G-K}{H}\quad(H>0)\tag{4}
$$

Positive expected net savings require q < q*. A threshold below zero means recovery is not needed to erase the gross benefit; a threshold above one means the modeled gross benefit exceeds all possible recovery incidence at the assumed H. The formula is algebra, not an estimate of q or H. It concerns monetary cost, not task utility: silent quality loss remains possible even with no recovery. Quality is a separate outcome in section 12; assigning it a monetary loss would require an explicit valuation and disjoint accounting, not an invented failure price. The audit's related-call frequency is not q.

![Figure 6](figures/v0.3/06-break-even.png)

Figure 6. Hypothetical break-even sensitivity: normalized net = 1 - q(H/G), with K = 0. Lines vary the assumed recovery cost. No observed follow-up rate is plotted on these curves; the causal probability and incremental cost have not been measured.

### A worked example, with assumptions exposed

Assume a removed block has 1,000 actual model tokens, appears 20 times, is first written at w = 1.25, and is read 19 times at alpha = 0.10. Gross avoided input cost is G = 1,000(1.25 + 19 x 0.10) = **3,150 base-price units**. If one causally additional recovery episode costs 10,000 of those units and K = 0, then q* = 0.315. At q = 0.10, expected net is +2,150; at q = 0.50 it is -1,850. These values are illustrative, not study measurements.

For actual bills, use the response usage categories and the price schedule effective at each call. Write total task cost as the sum over requests of $p_{\mathrm{uncached}}$ U + $p_{\mathrm{write}}$ W + $p_{\mathrm{read}}$ R + $p_{\mathrm{output}}$ O, plus any other billed fees. Model versions, caching tiers, long-context rules and subscription accounting must be recorded. A byte/4 meter cannot supply those terms.

## 7. What correctness can mean

### Proposition 2. Exact task preservation has a necessary and sufficient condition

Let x be a full tool observation, P(x) its compressed form, and g(x) the correct answer to a specified task. There exists a decoder h that recovers the answer from the compressed form exactly when g is constant on each set of inputs that P maps to the same output:

$$
\exists h:\ h(P(x))=g(x)\ \forall x\quad\Longleftrightarrow\quad\forall x,y:\ P(x)=P(y)\Rightarrow g(x)=g(y)\tag{5}
$$

**Proof.** Necessity: if P(x) = P(y), the decoder receives the same input, so h must return the same answer. Sufficiency: for each compressed output z, define h(z) to be the common g-value of any input mapped to z. Constancy makes this definition unambiguous.

**Counterexample for unrestricted truncation.** Choose two long outputs with identical retained head and tail, equal omitted line counts, and different omitted middle facts. A question about those facts has different correct answers, yet the compressed strings and markers can be identical. Therefore arbitrary lossy truncation cannot preserve every possible task. Even when an answer is preserved, a particular LLM may fail to extract it. This is an information-preservation condition, not an LLM performance guarantee. It can be checked on a finite fully specified domain or proved analytically for a known task function; arbitrary real-world task domains are not exhaustively testable from traffic. An interactive agent may recover lost information at additional cost, which this static decoder model does not cover. A task-outcome comparison remains necessary.

### Implementation evidence recorded for revision 0.2

**475 tests passed, 0 failed, 0 skipped** in a focused run of ten existing TokenSaver test classes on October 5, 2026, using .NET SDK 10.0.401. The selection covered cleanup, condensation, golden fixtures, command shaping, file-read budgets, line endings, and all three provider rewriters. The shared tree contained unrelated edits; no TokenSaver or Tests/TokenSaver edits were present before the run. The exact selection is in validation.json. [5]

| Property | Evidence and scope |
| --- | --- |
| Byte non-growth and untouched spans | Encoded-length gates in three rewriters (source inspection). Anthropic tests include adversarial escaping and changed-body byte checks; Codex also has whole-body no-op equality and shrinkage assertions, with narrower changed-body checks. |
| Determinism and idempotence | Bounded input corpora, repeated calls in one process and fixed configurations; the composition fixture runs Condense(Minify(x)), not the complete shipped plan. |
| Protocol preservation | Provider-specific parsing/rewriting fixtures and original-byte fallback. |
| Downstream task quality | Not established by these tests or by the historical trace audit. |

Idempotence of individual stages does not imply idempotence of their composition. The two-stage composition fixture excludes shape filters and file-read selection and enables nondefault ANSI/progress flags. Separate tests exercise those features, but this is not full-pipeline fuzzing or a universal invariant proof. The recorded filter excludes CliChatBodyTransformTests and the savings-store, publishing-job and route/accounting test classes. It cannot validate all meter plumbing. Historical research also reports a synthetic 108-combination sweep and zero compression-attributed divergences among 64 Claude Window-B cache misses with more than 50,000 written tokens and a predecessor within five minutes; the itemized causes are approximate, and neither Codex nor Window-C misses were covered. These are bounded samples, not universal guarantees. [1, sections 5.6 and 8.2]

## 8. A bounded September request audit

The expanded archive search found a September 8 mining report and its saved aggregates outside the main dossier, in an ignored experiment directory. Its extraction fixed the lower date at September 4 and the upper source row at 61,143, ending at 17:33:05 UTC on September 8. It retained 8,286 HTTP-200 model requests and 10 unsuccessful requests; model-list and count-token endpoints were excluded. The table and figure below use successful requests only. [9]

![Figure 7](figures/07-bounded-september-audit.png)

Figure 7. A separate bounded population: observed original-minus-forwarded UTF-8 request-body bytes. These exact saved aggregate counts produce the plotted rates. The interval overlaps Figure 3; do not pool the two populations or treat this as an independent controlled replication.

| Agent / requests | Original bytes | Removed bytes | Rate |
| --- | --- | --- | --- |
| Claude Code / 6,209 | 1,698,925,099 | 3,889,891 | 0.229% |
| Codex CLI / 1,843 | 1,532,592,995 | 21,603,929 | 1.410% |
| Grok CLI / 234 | 195,745,155 | 252,138 | 0.129% |

### Production-source replay on recorded outputs

The saved study replayed **8,177 distinct provider/tool/command/raw/after variants** through C# pipeline source from commit 2d4ee182. Four configurations covered defaults, ordinary cleanup, cleanup plus ANSI removal, and defaults plus ANSI removal: **32,708 variant/configuration cases**. The saved flags show zero character-growth, canonical-JSON-byte-growth, nondeterminism or idempotence failures, and the completed run reports no exceptions. This is historical corpus evidence, not a replay performed for this revision. Codex independently recounted the saved flags during the 0.3 review, without executing the pipeline. [9]

Inputs were recorded **after-texts**, with command metadata; character growth uses UTF-16 code units. Some replayed tool types were outside the live allowlist. The helper exercises the pipeline, not the complete HTTP adapter; canonical JSON is not each producer's escaping. Including after-text in the key preserves 28 raw-output groups with multiple transformed versions, which the older corpus could overwrite. Determinism here means repeating the same call in one process. Idempotence means P(P(after)) = P(after), which does not require P(after) = after. Forty-five allowlisted after-texts changed under the replay defaults: 44 whitespace-only differences and one new truncation removing 5,977 UTF-16 units. This review reproduced those comparisons from saved input/output JSONL. Builds, settings, pauses, metadata and adapter acceptance can differ from the original run; the differences do not by themselves establish nondeterminism or a cache miss. This does not establish task success.

## 9. File-read preservation and stage interactions

An earlier investigation discovered that shell-based file reads were being truncated despite dedicated file-read tools being out of scope. The ensuing mitigation widened the line budget for detected file reads. Offline replay of the production predicate over 261 previously elided outputs detected 205 for protection (78.54%); those outputs accounted for 2,960,996 of 4,056,231 characters removed before the fix (73.00%). [10]

![Figure 8](figures/08-file-read-mitigation.png)

Figure 8. Historical detector replay, captured traffic August 26-29. The left panel counts outputs; the right associates their historical removal with the detector decision. This is a predicate-positive rate on all sampled elided outputs, not recall or precision on a labeled file-read set, newly saved tokens or a task-quality effect. The panels use different units.

The August 30 rerun reproduced 205/261 on the same sample. An expanded 288-output sample detected 217 (75.35%), associated with 3,153,691 characters of historical removal; the reported size ceiling excluded none of these 217. These are different denominators, so the percentage change is not evidence of a detector regression. The source notes that no live soak had yet been completed. Known limits remain: `awk` is absent from the file-read predicate (three truncated Claude cases have T2 follow-ups in the saved audit), genuine diff/search outputs remain eligible, and recognized reads above 262,144 post-dedupe UTF-16 units revert to 150/50. The sample does not quantify detector recall or task protection. [10]

### Why an apparently safe stage needs a complete-pipeline check

The September 4 review found that an ANSI-removal Python mirror used outdated blank-line and file-read rules. After correction, its opportunity estimate was about 22.69 million replay-weighted characters: 9.63 million from ANSI plus ordinary cleanup and 13.06 million from newly enabled lossy condensation. Those are candidate replay estimates, not measured wire savings. A stage that removes control codes can enable downstream truncation; the entire resulting change cannot be credited to harmless formatting cleanup. [11]

The bounded September 8 production replay found only 4,830 additional replay-weighted UTF-16 code units from Codex ANSI cleanup over ordinary cleanup, across six outputs. A dedicated Read cleanup candidate offered 28,391 weighted code units for Claude. These are candidate opportunities in a different window, not comparable realized savings. They support testing each proposed expansion against the actual pipeline. [9]

## 10. Actual token usage is available, with limits

The September 8 extraction preserved provider-reported usage for every successful Claude request. This supplies a firmer accounting basis than characters divided by four. The following values cover the 6,209 Claude requests in Figure 7, including main and side requests. The categories describe actual usage under the observed system; there is no matching uncompressed token counterfactual. [9]

The known large-system/no-tools classifier family contributes 3,044/6,209 requests (49.0%), 167,222,501/558,868,027 input tokens (29.9%) and 4,634,900/8,733,135 one-hour write tokens (53.1%). This request mix is not a profile of ordinary coding turns. Its 381,468,024 decoded system characters provide a lower bound on its serialized bytes, not a measured family body total. If the family has zero eligible result removal, subtracting only that lower bound yields a conservative non-family reduction of at least 0.295%; exact family exclusion needs a matched body-byte subtotal and confirmation of zero result removal. The 5,545 September 4 requests quoted in the dossier come from a different extraction; this review cannot establish that 89% of the bounded audit occurred that day. [9]

![Figure 9](figures/09-actual-usage-buckets.png)

Figure 9. Actual provider-reported usage in the bounded September audit. Left: disjoint input categories; right: the cache-write category split by duration. The mix includes 49.0% classifier-family requests. Panels use different scales. Output tokens are reported separately in the table and are not included in the input bars.

| Reported category | Tokens |
| --- | --- |
| Uncached input | 408,496 |
| 5-minute cache writes | 9,985,648 |
| 1-hour cache writes | 8,733,135 |
| Cache reads | 539,740,748 |
| Total input | 558,868,027 |
| Output | 3,694,448 |

Duration buckets matter when pricing writes. A historical classifier-request family in the same study reported 4,634,900 cache-write tokens, all in the one-hour bucket. A blanket first-write factor of 1.25 cannot describe that family under a schedule that charges one-hour writes at a different rate. The illustrative equations in this paper keep rates explicit; a realized bill comparison must also retain each model's price and effective date. [6, 9]

The archive reports usage missing from 100 successful Codex responses and 9 successful Grok responses in this bounded sample. Usage composition for those consumers is therefore incomplete. The available Codex subset reports 168,536,064 cached input tokens out of 176,018,836 total input tokens (95.75%); cached tokens are a subset, not an additional input bucket. These are usage-weighted sums for responses with usage, not a full-population cache hit rate or a per-removed-block appearance count. Across all providers, exact observed usage alone still cannot attribute the difference caused by compression: the paired task experiment must supply that missing comparison.

## 11. Source reconciliation and uncertainty

Several source phrases require stricter definitions before use in a paper. The draft preserves the historical findings while correcting the following interpretations. These corrections are measurement clarifications; no product changes or raw-data re-extraction were performed.

| Source phrase or shortcut | Treatment in this paper |
| --- | --- |
| "Tokens saved" from BytesSaved / 4 | Estimated tokens only. Actual model token counts and billable usage are separate. |
| Audit KB / MB values | Thousands / millions of decoded characters where the scripts use Python len(text). Meter bytes remain UTF-8 bytes. |
| "Unique" tiered-audit removal | Marked result instances from one retained timeline per conversation; no global content-hash deduplication. |
| 11.2 MB = 0.23% of 6,154 MB | Not a valid equality: the script uses tool-result character delta for the size, but serialized-request character delta for the percentage. Omitted from the figures. |
| Single October 1 total | Initial meter and later daily reread are distinct snapshots. |
| "Refetch cost" | A related next-turn call is observed; causally additional work and recovered content are unknown. |
| "Lossless" cleanup | Normalization under restricted text assumptions; not byte reversibility or all-language semantic preservation. |

### Threats to validity

**Selection and dependence.** One developer and workstation supply the traffic. Provider, task, model and harness differences are entangled. Requests and results are correlated within tasks. The authors of the original analysis were themselves coding agents whose traffic entered the dataset. The observation period was not randomized.

**Incomplete trajectories and capture.** Conversation hashing and maximum-tool-count selection can omit branches and post-compaction segments. Command summaries stop at 400 characters; follow-up detection examines only the next turn. A separate backup audit documents possible capture loss, body-size limits and no durable inventory of capture gaps. Recorded traffic is not guaranteed to include every request. [12]

**Unknown counterfactuals.** The logs do not reveal the uncompressed task trajectory, recovered information, incremental recovery cost or eventual task quality. The dossier’s Study B sensitivity model reported Claude net -3.932 million and Codex net +18.105 million cost-equivalent token units, with a modeled Claude break-even near 19%. Its later price-factor correction applied to Codex; Claude’s provider-wide factor was not rechecked per model. Both modeled signs nevertheless depend on heuristic follow-ups being charged as whole additional turns, historical populations, character conversions and pricing assumptions. They are not measured net effects and their rates cannot be transferred to the newer tiered sample. The dossier favored disabling or A/B-testing Claude truncation, and the October 2 review recommended disabling it by default; current defaults still enable it. This historical recommendation is recorded, not implemented by this review. [1, sections 7.7-7.8, 9-10; 8]

**Freshness.** Current source checks and passing tests do not remeasure July-October workloads under today's configuration. No evidence here supports universal, product-wide or future-model savings rates.

## 12. An experiment to estimate task cost and quality

Compare complete tasks under a fixed harness, frozen repository, explicit acceptance criteria and prespecified analysis. The primary questions are whether compression reduces total task cost and preserves task success within an agreed noninferiority margin. Output replay alone cannot reveal changes in agent behavior. The estimand is the effect of rewriting conditional on the same proxy, launch configuration and harness, not the effect of running VibeRails versus no VibeRails. Gross byte shares are not expected task-cost effect sizes.

Feasibility requires a pilot. As an illustration only, a two-sided 5% test with 80% power for mean paired log-cost differences needs approximately n = [(1.96 + 0.842) sigma / abs(log(1 - s))]^2 independent task pairs. For s = 0.2% and sigma = 0.3, n is about 176,000; for s = 3.45% it is about 574. These assumed values show possible cost, not impossibility, and the log-cost estimand differs from the ratio-of-total-costs endpoint below. Size the final endpoint and quality margin using pilot-based simulation or another justified method, including repeated-task dependence, stopping rules and planned contrasts. Noninferiority also requires a margin and sufficient power.

A provider-compatible request-level token count can estimate equation 2 without establishing task effects. A fork at a truncation event with a fixed follow-up horizon can study short-term behavior, provided branch cache conditions and downstream censoring are controlled; it is not a complete-task substitute. Historical zero-rewrite and threshold-adjacent controls require separate validation.

| Design element | Prespecified requirement |
| --- | --- |
| Arms | A: proxy pass-through. B: `crlf-normalize`, `trailing-whitespace`, `blank-edges`, `blank-runs`, `git-status-group`, `grep-group`, `find-group`, with the same `scope-shell`/`scope-shell-background` scopes. C: B plus `elide-passed-tests`, `dedupe-lines`, `truncate-long`. Pin exact stage IDs and commit; B is restricted normalization/reshaping, not universal semantic equivalence. |
| Pairing and allocation | Run the same tasks from identical repository snapshots; randomize arm order and repeat stochastic runs. Pin provider/model, CLI, settings, tools and context budget. |
| Cache control | Define cold or standardized warm starts; isolate cache namespaces where supported; otherwise address cross-arm prefix reuse, counterbalance order and log actual write/read usage. Freeze settings; disable pause for the assigned policy or prespecify and record its handling. |
| Primary cost | All request and response usage through task completion or a fixed stop rule, including recovery, summaries, retries and review work included in scope. |
| Primary quality | Grade final repository artifacts and checks without treatment-revealing transcripts where possible; record any residual cues that could reveal allocation. Include incomplete runs and failures in the assigned arm. |
| Secondary outcomes | Wall time, tool calls, follow-up episodes, exact per-stage token/byte changes and preservation of critical evidence. |

For a task set with monetary costs $L_{k,A}$ and $L_{k,C}$, report the ratio of total costs, not the mean of task-level percentage reductions:

$$
\widehat{S}_{\rm cost}=1-\frac{\sum_{k=1}^{n}L_{k,C}}{\sum_{k=1}^{n}L_{k,A}}\tag{6}
$$

Resample tasks with all paired arm runs together; stratify by provider and task family. Requests within tasks are dependent. A pilot must estimate task-level variance and disagreement rates before setting the final sample size.

Let Q be task-success probability. Prespecify an acceptable margin delta and require the lower confidence bound for $Q_C$ - $Q_A$ to exceed -delta. Require both a positive lower bound on cost savings and this quality criterion before claiming reliable net savings without material quality loss. Report all assigned tasks; selecting successful runs alone can hide compression-induced failures.

Record the actual billing regime. API-priced usage, cash expenditure under a flat subscription and quota consumption are distinct outcomes; the workstation’s billing regime is not established by the package.

This is a proposed protocol. No randomized outcome experiment, new database logging, or paid model benchmark was initiated for this draft.

## 13. Interpretation and reproducibility

The evidence supports a precise claim: **accepted rewrites made the observed forwarded bodies smaller than the bodies submitted by the CLI.** Submitted bodies already contain any history, follow-ups and re-sends produced while compression was active. Thus the reported 0.20%, 3.45% and 0.18% shares are mechanical reductions within the selected meter population, not reductions relative to running tasks without TokenSaver. Accepted replacements cannot enlarge the request under the splice contract; the recorded focused regression run passed.

Net task economics remain unmeasured. Historical sensitivity models point negative for Claude and positive for Codex under their assumptions; neither sign is causally established. The newer follow-up classifier has no untruncated base rate and cannot distinguish recovery from ordinary next steps. These results motivate controlled comparisons, including a Claude truncation-off contrast, without changing product defaults here.

For context, the bounded September report counted 347,502,249 compact-JSON characters in definitions of tools with no correlated call in the captured histories. This is an opportunity ceiling, not removable bytes or measured savings; it cannot be divided by the 3,889,891 removed bytes to claim an 89-fold effect. The dossier’s MCP-disconnect cost comparison is likewise historical modeled context, not a compression-induced loss measured in this audit.

Public claims should retain dates, workload and denominators. Bytes/4 are estimated tokens, not billed usage; absent follow-ups do not prove correctness. The preservation theorem explains why quality evidence must be tied to specific tasks.

### Reproduction package

| File | Purpose |
| --- | --- |
| tokensaver-research-draft.tex | Standalone editable paper source; inline vector figure definitions and equations. |
| output/pdf/tokensaver-research-draft.pdf | Typeset reading copy generated from the same manuscript blocks. |
| evidence.json / daily-series.csv | Published aggregates, daily rows, computed ratios and explicit units. |
| source-manifest.json | SHA-256 hashes of source files, repository revisions and source locations. |
| validation.json | Exact focused test command, result, environment and calculation assertions. |
| build_paper.py / figures/ | Offline paper builder; publication figures in PDF, SVG and PNG. |
| output/docx/tokensaver-research-editable.docx | Word review copy with native equations and tracked future edits. |
| tokensaver-research-review.md / REVIEW.md | Agent-editable manuscript, stable claim IDs and return instructions. |

The table above lists the preserved 0.2 artifacts. The current edited manuscript is `tokensaver-research-v0.3.md`, with `evidence-v0.3.json`, `review-notes-codex.md`, `CHANGELOG.md` and a unified diff. Figures 1, 4 and 6 have new image files; other numeric figures are retained with revised captions and scope. `build_review_figures.py` rebuilds those three from packaged aggregates.

The historical 0.2 builder parses the dossier’s daily table and September 8 aggregates, recomputes ratios and produces nine figures. research-discovery.txt records the expanded search. No application database was queried; raw captured text is excluded. LaTeX embeds its plot data, while the PDF uses the same manuscript blocks and measurements.

Do not run the 0.2 builders over revised work. The 0.3 figure-only rebuild uses matplotlib; its input is the packaged aggregates and it does not query databases. Full Word/PDF/LaTeX exports and the ZIP remain 0.2 until deliberately regenerated from the accepted manuscript. Internal source paths are local provenance pointers. External publication needs a redacted, immutable aggregate archive and independently runnable benchmark.

## References and provenance

**[1]** VibeRails research archive. Does lossy tool-output compression save tokens, or just move them? A measurement dossier from 65 days of proxied coding-agent traffic. October 1, 2026. token_saver/research_2026-10-01.md; sections 3-9 and Appendix A.

**[2]** VibeRails research archive. Tiered follow-up analysis and extraction code. python-scripts/token_saver/tiered_rerun_audit.py (counts and character sums), scan_conversations.py (string lengths and retained timelines), timeline_stats.py (composition and daily grouping). Saved corrected aggregate: results/vibe6-paper/tiered_rerun_2026-08-30_20261001_201359.md.

**[3]** VibeRails source. TokenSaver/Minify/AnthropicMessagesRewriter.cs:188, 201, 229; CodexResponsesRewriter.cs:209; ChatCompletionsRewriter.cs:176. Encoded replacement gates, accepted traces and fallback copying. TokenSaver/Minify/OutputCondenser.cs:54-57, 82, 144-170, 263, 351. Budgets, line collapse and truncation.

**[4]** VibeRails source. VibeRails.Data.Abstractions/DB/ITokenSavingsStore.cs:9-26. Aggregate byte accounting and integer BytesSaved / 4 estimate. TokenSaver/Pipeline/CompressionCatalog.cs:123-185 and 201-243, default stages/scopes; TokenSaver/LlmProxyRelay.cs:140-158 and 212-226, meter versus log; Minify/*BodyTransform.cs, path/content-type/body/cap filters; VibeRails/Services/LlmProxy/LlmProxySettingsService.cs:92-107, pause state.

**[5]** VibeRails tests. Tests/TokenSaver/OutputMinifierTests.cs:487; OutputCondenserTests.cs:319; PipelineGoldenFixtureTests.cs:107; ShapeFilterTests.cs:877; file-read, CRLF and provider-rewriter test classes. Focused run October 5, 2026: 475 passed, no failures or skips. Full filter in validation.json.

**[6]** Anthropic. Prompt caching. Official developer documentation; accessed October 5, 2026. Used for exact-prefix requirements and distinct cache write/read prices, with model exceptions.

https://platform.claude.com/docs/en/build-with-claude/prompt-caching

**[7]** Anthropic. Token counting. Official developer documentation; accessed October 5, 2026. Counts depend on the selected model and may differ slightly from actual message usage.

https://platform.claude.com/docs/en/build-with-claude/token-counting

**[8]** VibeRails research archive. Token saver effectiveness review, October 1, 2026, with October 2 follow-up. token_saver/review_2026-10-01.md. Historical interpretation and implementation handoff; the more cautious consolidated dossier governs empirical claims in this draft.

## References and provenance (continued)

**[9]** VibeRails research archive. TokenSaver mining, September 8, 2026. python-scripts/token_saver/results/codex-mine-0908/findings.md and summary.json; saved replay-output.jsonl numeric flags. Production-source diagnostic: python-scripts/token_saver/replay_pipeline/Program.cs. Bounded request counts, replay invariants, usage buckets and candidate comparisons.

**[10]** VibeRails research archive. truncate-long was cutting holes in source files: findings and fix. token_saver/truncation_file_reads.md, sections 4-6. August 29-30, 2026. Production-predicate replay and sample-specific protection coverage.

**[11]** VibeRails research archive. V1 TokenSaver findings: review of Claude's Codex analysis. token_saver/v1_findings_claude_codex_response.md, September 4, 2026, sections 2-7. Corrected cost assumptions and ANSI-mirror estimates. Candidate estimates are not treated as realized wire savings.

**[12]** VibeRails research archive. Backup coverage audit, September 14, 2026. vibe-data/docs/backup-coverage-audit.md, proxy-database and capture-limit sections. Archival Brotli ratios concern storage and are excluded from TokenSaver efficacy claims.

### Source fingerprint

Consolidated dossier SHA-256: 94c99e4fd879a801411bff602bf754df18c59db9032a21e76741e62acdf0d11d

The complete file-level source manifest accompanies the paper. Source line references identify the checked local revision and may move in later versions. Internal research sources are not peer-reviewed publications. Historical aggregates were reused and recalculated. The focused test record and original exports belong to 0.2; the 0.3 review independently checked source hashes, source logic, aggregate arithmetic and saved replay comparisons. It did not run new model experiments or reopen application databases. Current external price schedules and source-export layout were not revalidated in this reconciliation.
