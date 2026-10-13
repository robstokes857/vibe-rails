# TokenSaver: free token savings on every request

TokenSaver tidies the output of every shell command before your coding agent sends it to the
model. Trailing spaces, extra blank lines and Windows line endings go. `git status`, `grep` and
`find` output gets grouped. Repeated lines collapse into one line with a count, and long runs of
passing tests become a count too, while failures stay exactly as they were. Your agent still sees
everything it's working with, so it never has to go back for anything. There's nothing to set up
and nothing to lose.

**Saved on every turn.** Coding agents resend the whole conversation with every message. Once
TokenSaver trims something, it stays trimmed in every request for the rest of the session.

**What it adds up to.** Across 26 days of one developer's real work in September and October
2026, TokenSaver kept about **7.2 million tokens** from being sent: 5.9 million from Codex CLI
and 1.3 million from Claude Code. That's roughly **280,000 tokens a day** for a single developer,
or about 0.3% of everything Codex sent and 0.05% of Claude Code's. It costs nothing, needs no
extra turns, and never breaks the provider's prompt cache.

**We measure what we ship.** TokenSaver used to cut the middle out of very long outputs as well.
When we measured it, Claude Code went back for the missing lines about 70% of the time, which
cancelled the saving. So we turned that stage off. Everything TokenSaver does now is a free saving.

**Next: adaptive token savings.** We're building savings that decide, output by output, what's
safe to shorten. We'll publish the numbers once we've measured them the same way.

---

*Measured on one developer's workstation: September 1–16 and October 1–13, 2026, about 38,000
requests. Tokens are estimated from characters (3.6 per token) and counted in every request
that carried them, the way input is billed. The "tokens saved" counter on viberails.ai adds up
all reporting installations, which is a different population. Method and full results: "Free
Savings: What TokenSaver Removes Without Hiding Anything", draft 0.1.*
