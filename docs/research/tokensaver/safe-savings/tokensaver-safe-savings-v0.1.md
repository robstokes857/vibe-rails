# Free Savings: What TokenSaver Removes Without Hiding Anything

Measured on one developer's coding-agent traffic, September–October 2026

DRAFT 0.1 | 13 OCTOBER 2026 | VibeRails research

This draft starts the paper that replaces the earlier TokenSaver research drafts (0.2 and 0.3) as
the public account of what TokenSaver saves. It reports only savings that need no recovery, and
it explains why the one stage that cuts content is now off by default.

### Abstract

TokenSaver is a local proxy that sits between coding-agent command-line tools and their model
providers. It tidies tool output in each request before the request is sent. This paper counts
only the stages that leave every content line visible: line-ending, trailing-space and blank-line
cleanup; grouping of `git status`, `grep` and `find` output; and collapsing runs of identical
lines into one line with a count.

On one developer's traffic (September 1–16 and October 1–13, 2026; 22,789 Codex CLI and
15,511 Claude Code requests), these stages removed an estimated **0.29% of Codex
input tokens** and **0.05% of Claude Code input tokens**. Priced with the usage each provider
reported, that is 0.33% and 0.046% of cost. In absolute terms, about 7.2 million tokens were
never sent over 26 days, roughly 280,000 a day for a single developer. These savings are modest,
and they are free: no content line is removed, so the agent never has to fetch anything back.

The one stage that does remove content, truncation of long output, is now off by default. When it
cut a long Claude Code output, the cut lines came back into the conversation within three turns in
69–74% of cases, against 3% for long output that was not cut. That made the stage roughly
break-even on Claude Code.

| Claim | Evidence status |
| --- | --- |
| Safe stages removed the amounts in Section 5 | Measured per request on this workstation's traffic |
| Safe stages never remove a content line | By construction; the exceptions are listed in Section 2 |
| Truncation triggered re-fetches on Claude Code | Measured by content against a comparison group |
| The same savings for other users or workloads | Not established; this is one developer's traffic |

## 1. What TokenSaver does

Coding agents run shell commands and send the output back to the model as tool results. They
also resend the whole conversation on every turn, so a long tool result is paid for once when it
first appears and again, usually at a discounted cache-read price, on every later turn.

TokenSaver rewrites selected tool results in place before the request leaves the machine. It
touches only the output of allowlisted shell tools. System prompts, the user's messages and the
agent's own tool calls are never changed, and neither are the provider's responses. Every stage
is deterministic and idempotent, so the same output is rewritten the same way on every turn. A
replacement is accepted only if it makes the encoded request smaller.

| Stage | Default | What it changes |
| --- | --- | --- |
| Line endings, trailing spaces, blank lines at the edges, runs of blank lines | on | Whitespace only |
| `git status`, `grep -n` and `find` grouping | on | Reshapes lines (for example, file names stated once per group); every entry stays |
| Repeated lines | on | Three or more identical consecutive lines become one line tagged `[xN]` |
| Passing-test runs | on | Consecutive passing-test lines from recognized test runners become `[... N passed ...]`; failures, skips and summaries stay |
| Truncate long output | **off** (was on until October 2026) | Kept the first 150 and last 50 lines and replaced the middle with `[... N lines elided ...]` |

## 2. What "safe" means here

A stage is safe in this paper if every line of content stays visible to the model, so there is
nothing left to fetch back. Whitespace cleanup and grouping change formatting. Repeated-line
collapse keeps the line and records how many times it occurred.

Three exceptions should be stated plainly:

- **Whitespace can be the point.** If the agent is looking for trailing spaces or mixed line
  endings, for example in a diff of a whitespace-only change, cleanup hides exactly what it wants.
- **Passing-test condensing drops test names.** It keeps failures, skips, summaries and the count
  of passing tests, but the names of individual passing tests are gone. It never fired in the
  measured windows, so it contributes nothing to the numbers below.
- **Truncation is not safe.** It removes the middle of long output, which is why it is reported
  separately (Section 6) and now off by default.

## 3. Data and populations

All measurements come from **one developer's workstation**, using Claude Code and Codex CLI on
the developer's own repositories. The proxy keeps an exchange log of every request before and
after rewriting, and the provider's response including token usage. Retention removes logged
exchanges a week after their session is backed up, so this paper uses the days still on disk:

| Window | Claude Code requests | Codex CLI requests |
| --- | --- | --- |
| September 1–16 | 10,894 | 7,451 |
| October 1–13 (October 2, 4 and 5 not retained) | 4,617 | 15,338 |
| Total | 15,511 | 22,789 |

Requests are inference calls that returned HTTP 200 with a buffered body. Model-list calls,
token-count calls, failed calls and bodies over the 10 MiB buffering limit are excluded.

**The website's aggregate "tokens saved" counter is a different population.** It sums the meters
of every VibeRails installation that reports one. This paper studies one workstation in detail and
makes no claim about the aggregate.

## 4. How savings are measured

Each rewritten tool result is assigned to one stage kind by the markers its rewrite added:
truncation if it gained an elision marker, passing tests or repeated lines if it gained their
markers, and cleanup otherwise. When truncation and cleanup both changed one result, the whole
difference counts as truncation, so the safe totals are a slight undercount.

Three measures are reported:

1. **Share of request characters.** Characters removed from each request body, counted in every
   request that carries them, divided by all request characters. This is close to what the
   VibeRails meter counts. For Codex it understates the share of tokens, because Codex request
   bodies also carry large encoded reasoning blobs that contain few tokens per character.
2. **Estimated share of input tokens.** Removed characters divided by 3.6 characters per token,
   divided by the input tokens the provider reported (cached tokens included). The 3.6 figure is an
   estimate for tool output text, not a tokenizer count.
3. **Share of cost.** Removed tokens priced in each request that carried them: at the request's
   cache-write price when the content was new, and at the cache-read price (0.1) when it was a
   resend and the request hit the cache. Spend is every request's reported usage at the same
   ratios. Claude Code: uncached input 1, 5-minute cache write 1.25, 1-hour cache write 2, cache
   read 0.1, output 5. Codex CLI: uncached input 1, cached input 0.1, output 8. These are ratios to
   the base input price, not dollars, and the Codex traffic ran on a subscription, so for Codex this
   measures usage rather than a bill.

## 5. Results: what the safe stages removed

| Agent | Window | Share of request characters | Estimated share of input tokens | Share of cost |
| --- | --- | --- | --- | --- |
| Codex CLI | September 1–16 | 0.339% | 0.617% | 0.748% |
| Codex CLI | October 1–13 | 0.059% | 0.089% | 0.086% |
| Codex CLI | both | 0.176% | 0.286% | 0.332% |
| Claude Code | September 1–16 | 0.046% | 0.039% | 0.037% |
| Claude Code | October 1–13 | 0.078% | 0.071% | 0.059% |
| Claude Code | both | 0.060% | 0.052% | 0.046% |

By kind, over both windows:

| Agent | Kind | Results changed | Characters removed (on the wire) |
| --- | --- | --- | --- |
| Codex CLI | cleanup and grouping | 15,110 | 20.81 M |
| Codex CLI | repeated lines | 21 | 0.59 M |
| Claude Code | cleanup and grouping | 2,694 | 4.30 M |
| Claude Code | repeated lines | 26 | 0.25 M |

Passing-test condensing changed no results in either window.

**Why so little?** Most of a coding agent's input is not shell output. It is the system prompt,
tool definitions, the conversation so far and, for Claude Code, file reads through its Read tool,
which TokenSaver does not touch. Cleanup only removes formatting from the shell output that remains.
Claude Code also caps Bash output itself, so very long outputs never reach the proxy whole.

**Why "free"?** Nothing the agent needs is removed (Section 2), so there is no re-fetch to pay for.
The rewrite is deterministic, so it never breaks the provider's prompt cache between turns.

## 6. Why truncation is off

Until October 2026, truncation was on by default. It fired on outputs whose middle exceeded
10 lines and 4,096 characters. For Claude Code that was about once a day, mostly on `git diff`,
`git show` and `git log`.

We tested whether the agent went back for what was cut. Our test compared content, not commands.
For each truncated result we took the nontrivial lines the model could not see (the cut middle)
and the lines it could see (head and tail). We then checked how many of each came back in the tool
output the agent received over its next three turns. As a comparison group, long outputs that were
*not* cut were split the same way: first 150 and last 50 lines as "head and tail", the rest as a
middle the model did see.

| Group | Results | Cut lines came back within 3 turns |
| --- | --- | --- |
| Claude Code, truncated (Sep 1–16, Oct 1–13) | 26 | 69% |
| Claude Code, truncated (Jul 29 – Oct 1, re-scored from archived outputs) | 144 | 74% |
| Claude Code, long output not truncated | 439 and 883 | 3% |
| Codex CLI, truncated (Sep 1–16, Oct 1–13) | 186 | 24% |
| Codex CLI, long output not truncated | 875 | 6% |

Lines from an unrelated conversation matched 0.3% of cut lines on average, so a match is not an
accident of the method.

**On Claude Code the re-fetches cost about what the cut saved.** Truncation removed 276,000
cost-equivalent tokens across 26 truncations. After subtracting the re-fetched content and the
extra turns some re-fetches needed, it came out between −36,000 and +145,000. On the older events
the range was −755,000 to +658,000 on 1,541,000 removed. In 31% of cases the agent carried on
without ever seeing the cut lines, which may or may not have mattered.

**On Codex CLI truncation paid for itself** in the measured windows. After subtracting re-fetched
content and extra turns, it saved between 4.5 and 5.7 million cost-equivalent tokens out of 6.5
million removed. It is off there too, so that the savings this paper reports need no recovery at
all. Measuring a safe, adaptive replacement is future work (Section 9).

## 7. Codex savings fell in October because the models changed

The VibeRails meter showed Codex savings falling from 3.38% of request bytes (September 4 – October 1)
to 0.64% (October 2–13). The request bodies record which model each request asked for. Nearly all
of September's truncation came from `gpt-5.6-sol`. `gpt-6-astra` almost never produced output long
enough to cut, at any speed setting:

| Window | Model, effort, service tier | Share of Codex requests | Truncated results | Truncation share of characters |
| --- | --- | --- | --- | --- |
| Sep 1–16 | `gpt-6-astra`, xhigh, priority | 29.8% | 1 | 0.010% |
| Sep 1–16 | `gpt-5.6-sol`, max, default | 23.2% | 42 | 3.813% |
| Sep 1–16 | `gpt-6-astra`, xhigh, default | 21.9% | 0 | 0.000% |
| Sep 1–16 | `gpt-5.6-sol`, xhigh, priority | 10.8% | 84 | 11.238% |
| Sep 1–16 | `gpt-5.6-sol`, max, priority | 7.3% | 19 | 2.622% |
| Oct 1–13 | `gpt-6-astra`, xhigh, ultrafast | 37.6% | 11 | 0.191% |
| Oct 1–13 | `codex-auto-review`, low, default | 19.6% | 0 | 0.000% |
| Oct 1–13 | `gpt-6.1-sol`, xhigh, priority | 11.6% | 3 | 0.120% |
| Oct 1–13 | `gpt-6.1-sol`, xhigh, default | 11.3% | 10 | 0.491% |
| Oct 1–13 | `codex-auto-review`, low, priority | 4.9% | 0 | 0.000% |

October's most common setting was `gpt-6-astra` at `xhigh` effort with the `ultrafast` service
tier. Across all Codex models, 91% of October outputs over 8 KB had fewer than 210 lines, against
64% in September. A line-based cut cannot shorten output that arrives as a few long lines.

## 8. Reading the VibeRails meter

The meter in VibeRails counts bytes removed from each request, including every resend of earlier
history, and shows them as estimated tokens (bytes divided by 4). That is a real count of bytes not
sent. It is not the same as content removed, which is smaller because each removal is counted once
per resend. Nor is it a bill, because most resent tokens are cache reads at a tenth of the price.
With truncation off, the meter will show only the safe stages, and its numbers will be smaller.

## 9. What is next

- **Adaptive token savings.** We are working on stages that decide, per output, what is safe to
  shorten. This paper makes no claim about them; they will be measured the same way before any
  number is published.
- **Model capture.** The exchange log will record the requested model, reasoning effort and service
  tier, so savings can be reported per model without re-reading request bodies.
- **Re-measurement after the change.** Once truncation is off on live traffic, the safe-stage share
  will be measured again directly rather than separated from mixed rewrites.

## 10. Limitations

- **One developer, one workstation, two windows.** Workload, models and habits changed between
  September and October. Nothing here estimates savings for other users.
- **Estimated tokens.** Removed tokens use 3.6 characters per token, not a tokenizer count.
- **Fixed price ratios.** Cost shares use fixed ratios, not each model's current price list, and
  the Codex traffic was not billed per token.
- **Re-fetches are judged from content overlap.** A targeted fetch of exactly the cut lines is hard
  to explain otherwise, and the comparison group supports that reading. Broad re-reads are more
  ambiguous.
- **Task quality was not measured.**

## Appendix A. Daily safe-stage shares

| Day | Agent | Requests | Safe share of input tokens (est.) | Safe share of cost | Truncation share of characters |
| --- | --- | --- | --- | --- | --- |
| 2026-09-01 | Claude Code | 14 | 0.009% | 0.006% | 0.000% |
| 2026-09-01 | Codex CLI | 273 | 1.296% | 0.985% | 6.669% |
| 2026-09-02 | Claude Code | 58 | 0.099% | 0.111% | 0.000% |
| 2026-09-02 | Codex CLI | 32 | 1.141% | 2.529% | 2.183% |
| 2026-09-03 | Claude Code | 367 | 0.094% | 0.088% | 0.000% |
| 2026-09-03 | Codex CLI | 1,503 | 1.188% | 1.792% | 3.198% |
| 2026-09-04 | Claude Code | 5,545 | 0.034% | 0.035% | 0.160% |
| 2026-09-04 | Codex CLI | 195 | 0.688% | 0.575% | 3.810% |
| 2026-09-05 | Claude Code | 222 | 0.091% | 0.072% | 0.000% |
| 2026-09-05 | Codex CLI | 344 | 0.083% | 0.200% | 0.055% |
| 2026-09-06 | Claude Code | 111 | 0.019% | 0.014% | 0.760% |
| 2026-09-06 | Codex CLI | 395 | 0.360% | 0.271% | 0.673% |
| 2026-09-07 | Claude Code | 53 | 0.025% | 0.021% | 0.000% |
| 2026-09-07 | Codex CLI | 771 | 0.238% | 0.424% | 0.036% |
| 2026-09-08 | Claude Code | 485 | 0.011% | 0.010% | 0.000% |
| 2026-09-08 | Codex CLI | 168 | 0.685% | 0.510% | 10.905% |
| 2026-09-09 | Claude Code | 539 | 0.015% | 0.014% | 0.000% |
| 2026-09-09 | Codex CLI | 216 | 0.011% | 0.019% | 0.000% |
| 2026-09-10 | Claude Code | 341 | 0.041% | 0.042% | 0.299% |
| 2026-09-10 | Codex CLI | 334 | 0.778% | 0.684% | 5.439% |
| 2026-09-11 | Claude Code | 114 | 0.030% | 0.029% | 0.032% |
| 2026-09-11 | Codex CLI | 111 | 0.055% | 0.046% | 0.000% |
| 2026-09-12 | Claude Code | 853 | 0.097% | 0.074% | 0.098% |
| 2026-09-12 | Codex CLI | 65 | 0.002% | 0.004% | 0.000% |
| 2026-09-13 | Claude Code | 202 | 0.034% | 0.037% | 0.285% |
| 2026-09-13 | Codex CLI | 249 | 0.302% | 0.373% | 10.203% |
| 2026-09-14 | Claude Code | 425 | 0.035% | 0.030% | 0.000% |
| 2026-09-14 | Codex CLI | 1,228 | 0.171% | 0.194% | 1.625% |
| 2026-09-15 | Claude Code | 1,024 | 0.031% | 0.025% | 0.124% |
| 2026-09-15 | Codex CLI | 642 | 0.408% | 0.396% | 2.490% |
| 2026-09-16 | Claude Code | 541 | 0.034% | 0.032% | 0.000% |
| 2026-09-16 | Codex CLI | 925 | 0.742% | 0.950% | 0.887% |
| 2026-10-01 | Claude Code | 367 | 0.030% | 0.028% | 0.000% |
| 2026-10-01 | Codex CLI | 566 | 0.356% | 0.270% | 1.525% |
| 2026-10-03 | Codex CLI | 93 | 0.013% | 0.011% | 0.000% |
| 2026-10-06 | Claude Code | 373 | 0.092% | 0.081% | 0.000% |
| 2026-10-06 | Codex CLI | 1,710 | 0.157% | 0.118% | 0.118% |
| 2026-10-07 | Claude Code | 172 | 0.119% | 0.080% | 0.000% |
| 2026-10-07 | Codex CLI | 995 | 0.066% | 0.048% | 0.020% |
| 2026-10-08 | Codex CLI | 2,529 | 0.028% | 0.024% | 0.060% |
| 2026-10-09 | Claude Code | 330 | 0.074% | 0.059% | 0.177% |
| 2026-10-09 | Codex CLI | 2,050 | 0.070% | 0.054% | 0.672% |
| 2026-10-10 | Claude Code | 334 | 0.169% | 0.119% | 0.188% |
| 2026-10-10 | Codex CLI | 2,132 | 0.075% | 0.098% | 0.026% |
| 2026-10-11 | Claude Code | 634 | 0.044% | 0.041% | 0.000% |
| 2026-10-11 | Codex CLI | 1,680 | 0.107% | 0.172% | 0.148% |
| 2026-10-12 | Claude Code | 1,293 | 0.066% | 0.052% | 0.000% |
| 2026-10-12 | Codex CLI | 2,547 | 0.080% | 0.080% | 0.241% |
| 2026-10-13 | Claude Code | 1,114 | 0.063% | 0.056% | 0.146% |
| 2026-10-13 | Codex CLI | 1,036 | 0.014% | 0.011% | 0.000% |

## Appendix B. Evidence

`evidence-v0.1.json` beside this draft holds every aggregate used here, with no commands, paths or
tool output. The analysis reads raw request logs that contain source code and prompts. Those logs
stay on the workstation.
