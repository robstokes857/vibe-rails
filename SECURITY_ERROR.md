# Security finding dispositions — VIBE-182 (2026-10-21)

Source: card VB-RS8DU-252 / VIBE-180, attachment `security-scan-2026-10-21.md`.
Fix card: VB-R5WOA-254 / VIBE-182. All eleven findings below are agreed and fixed in
local main. Validation and independent Board review status are recorded on the fix card. The original scan remains
attached to VIBE-180. No database schema or stored data is changed.

| Finding | Disposition and evidence |
| --- | --- |
| 1. Account key, Remote Access, relay and PIN writes lacked presence confirmation | Fixed. Writes are root-only. Sensitive settings changes and PIN set/clear require the current PIN, or a random dashboard confirmation code when no PIN exists. Codes expire after two minutes and are single-use; five failed proofs impose a five-minute cooldown. CLI contacts cannot obtain challenges or write Settings. Key replacement emits a fixed warning/event. See `SettingsPresenceService`, `AppSettingsRoutes`, `PinRoutes` and Settings UI. |
| 2. Prompt text could become CLI options | Fixed. Claude/Codex/Grok prompts always follow a fresh `--` in shell and direct argv paths. Named-prompt CLIs reject leading-dash messages. `CommandServiceSecurityTests` exercises hostile summaries and both launch forms. |
| 3. CLI descendants inherited dashboard credentials | Fixed. `AgentCredentialService` issues separate paired capabilities. Root tool contacts rotate per launch and bind to an active, reachable owning tab/session. Only picker, Board launch and owned-session sharing routes are allowed. Proxy/control contacts rotate per PTY session and are revoked on exit. Contacts cannot call Settings, terminal APIs, shutdown, WebSockets or HTTP MCP. Native stdio Board tools still operate with the local OS user's authority; this is not an OS sandbox. |
| 4. Public JSON could grant Board tools | Fixed. `AuthorizeBoardTools` is ignored in both JSON directions. Only authenticated root-to-child launch headers carry the server's decision and fresh scoped tool contact. JSON, carrier and grant regressions cover the boundary. |
| 5. Approved scripts could discover a repository Python interpreter | Fixed. Both captured and interactive runs use `DiscoverPythonRunnerOptions` rooted at the installed scripts directory, excluding PYBRIDGE_PYTHON and active-venv overrides. A planted repository interpreter regression verifies selection of the trusted scripts runtime. |
| 6. ShellExecute could resolve pwsh/code from a repository | Fixed. Jobs and windowed steps resolve absolute pwsh.exe from absolute PATH entries, with directory changes inside the quoted script. Sandbox shells do the same; VS Code reuses its application resolver. The PowerShell regression executes hostile path characters and checks argv and directory. |
| 7. Environment and agent terminal scope gaps | Fixed. Terminal/sandbox starts require the saved environment ID, check provider and project visibility, and never fall back to a name. Board and frontend launches carry IDs. Agent terminal routes are root-only and unavailable to scoped CLI contacts, so an agent cannot type into another tab. UI credentials retain the dashboard's ordinary multi-tab control. |
| 8. validate_vca accepted a checkout path | Fixed. The public MCP method takes no parameters and derives its checkout from the process. Internal validation helpers retain server/test-supplied snapshots and paths. |
| 9. Provider relay followed redirects | Fixed. The relay uses the dedicated `llm-proxy` client with redirects disabled. The real DI handler and relay client selection have regressions. |
| 10. Public card shares included other-project linked-card metadata | Fixed. Capture uses the project-filtered publication projection rather than the broader local related-card list. A foreign-title regression checks the serialized payload. |
| 11. OPTIONS exposed proxy feature state | Fixed. All five proxy handlers authenticate both headers before reading feature settings, including OPTIONS. Missing/incomplete credentials always receive 401. |

Dashboard confirmation is deliberately scoped: a page already executing arbitrary same-origin
JavaScript with the full dashboard pair can read a no-PIN challenge. The scoped CLI pair is
excluded; this does not claim to isolate an agent already able to edit the user's settings files.

Validation: development build; full .NET suite (6,123 passed, 17 skipped); 1,065 frontend tests; both
API_SEC.md listener sweeps (approved Program.cs host and PortFinder probe; other hits are
fixtures). Independent review results are recorded on the fix card.
This is a targeted remediation, not a fresh audit of every endpoint or every recommendation
in the source scan. Unlisted scan hardening notes are not silently marked resolved here.

Independent review follow-up (`a5518288526847efbab316b17a8a5e51`): both medium-high
correctness findings are agreed. F1 now forwards the selected stable environment ID from the
script workbench. F2 now prepares an unbound replacement contact, promotes it only after
a confirmed session start, and discards rejected candidates while preserving the live contact.
Production host/context regressions cover sequential and concurrent duplicate starts followed
by an accepted replacement; the middleware also rejects unconfirmed candidates. Fresh review
results are recorded on VIBE-182.
