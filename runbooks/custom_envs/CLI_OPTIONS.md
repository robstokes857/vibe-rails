# Uses

This describes the entire custom env option workflow but it has mainly been used for adding new models to the LLM/CLI provider

# Custom Environment CLI Options

This runbook explains how to change, add, or delete custom environment options
for the managed TUI CLIs: Claude, Codex, Antigravity, Copilot, OpenCode, and the
OpenCode-backed pseudo-CLIs GLM 5.2 and GLM 5.3, plus native Grok 4.6.

Use it when editing the Environments page's create/edit modal, the per-CLI
settings APIs, or the generated launch arguments stored in `CustomArgs`.

## CLI References

Use these upstream CLI references to verify exact option names, accepted values,
and launch behavior before changing generated arguments:

- Claude: https://code.claude.com/docs/en/cli-reference
- Claude settings: https://code.claude.com/docs/en/settings
- Codex: https://developers.openai.com/codex/cli/reference
- Codex config: https://developers.openai.com/codex/config-reference
- Codex models: https://developers.openai.com/codex/models
- Antigravity (agy): https://antigravity.google/docs/cli-using (also `cli-getting-started`, `cli-settings`)
- Antigravity authoritative flags: run `agy --help` (and `agy models` for the live model catalog)
- Copilot: https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference
- OpenCode CLI: https://opencode.ai/docs/cli/
- OpenCode config: https://opencode.ai/docs/config/
- OpenCode models: https://opencode.ai/docs/models/
- Grok (native Build CLI): run `grok --help` (authoritative flags; `--reasoning-effort` / `--effort`)

Status re-checked on August 23, 2026 against the upstream references above plus
live CLIs on this host: Claude Code 2.1.241, Codex CLI 0.148.0 (`codex debug
models`), Copilot CLI 1.0.71, agy 1.1.7 (`agy --help`), OpenCode (`opencode
--help` / `opencode models`). The Codex pinned list stays intentionally
narrower than the live catalog (owner request, July 10, 2026). agy third-party
write-ups still conflict — trust `--help`. `agy models` still prints nothing
in a non-TTY, so that catalog is still hand-copied.

Important distinction: `CustomArgs` and `CustomPrompt` are VibeRails' launch
contract. The DTO field lists below describe fields VibeRails currently
round-trips through its settings APIs; they are not a complete upstream settings
schema. When writing physical CLI config files, prefer current upstream config
keys.

Compatibility policy: do not maintain backwards compatibility for old or
unsupported CLI config-file keys. If an upstream TUI no longer accepts an old
setting, an existing environment using that setting is already broken. Write the
current upstream keys, remove old VibeRails-managed keys on save, and let users
delete/recreate broken environments instead of adding migration code.

Permissions policy — YOLO or nothing: VibeRails does NOT manage granular
permission, approval, or sandbox settings from the UI. Each managed CLI exposes a
single "YOLO Mode" toggle and nothing else for permissions. YOLO is a launch flag
stored in `CustomArgs` (never a settings-file key); when it is off, VibeRails
leaves the CLI's permission configuration completely untouched. Concretely, the
settings services must never read, write, or remove these native keys:

- Claude: `permissions.*` (`permissions.allow`, `permissions.deny`, `permissions.defaultMode`).
- Codex: `approval_policy`, `sandbox_mode`, `model_provider`.
- Antigravity (agy): none — agy is launch-flag-only (no settings file), so there
  are no native settings keys to manage. YOLO is the `--dangerously-skip-permissions`
  launch flag.

Why this rule exists: a settings-file key that the UI cannot fully drive becomes
destructive. A previous build "managed" `permissions.allow` while no UI control
ever populated it, so every save deleted the user's curated allow list. Never
write or delete a settings-file key unless a UI control fully owns its value;
otherwise leave it alone (preserve it like any unknown field).

## Model Lists (hand-maintained — refresh these)

The Claude, Codex, Antigravity, and Copilot **Model** dropdowns are pinned, hand-curated lists. They do
not auto-discover models, so this runbook is the place that owns them: when an
upstream model ships or is retired, update the list here and in code. This is the
main reason this section exists — a future change ("Claude shipped 4.9", "Haiku
4.5 was removed") should land here.

Where they live in code
(`VibeRails/wwwroot/js/modules/environment-controller.js`):

- Claude: `renderClaudeModelOptions()`.
- Codex: `renderCodexModelOptions()`.
- Antigravity: `renderAntigravityModelOptions()`.
- Copilot: `renderCopilotModelOptions()`.
- OpenCode: `renderOpencodeModelOptions()`.

Model addition completion checklist:

1. Update the matching dropdown renderer and this runbook in the same change.
2. Update and run the existing model-list UI test for the affected CLI. For Codex,
   [UITests/tests/environment-codex.spec.js](../../UITests/tests/environment-codex.spec.js)
   checks the pinned model values and their priority order. Run it with
   `npm --prefix UITests run test:e2e -- tests/environment-codex.spec.js`.
3. Verify the model appears when creating a **new** environment, in the intended
   position after Default. An existing environment displaying it as `(custom)`
   does not prove it was added to the pinned dropdown list.
4. Select the model, save, and reopen the environment. Confirm the exact model ID
   is preserved and the generated `CustomArgs` contains the expected `--model`
   argument.
5. Verify against the updated app build and refresh the UI so it loads the changed
   JavaScript. State whether the change is only in source or is also installed in
   the running app; a source edit alone does not update an installed copy.

Syntax checks and a successful build cannot detect a missing dropdown entry.
If the UI check cannot run, report that verification gap explicitly.

Current pinned values (Claude re-verified 2026-09-01; Codex 2026-09-04;
Copilot 2026-08-23; OpenCode 2026-08-23; Antigravity flags 2026-08-23, models
still last hand-copied 2026-07-02):

- Claude (full model IDs): `claude-fable-5-1`, `claude-fable-5`,
  `claude-opus-5`, `claude-opus-4-8`, `claude-opus-4-7`, `claude-sonnet-5`,
  `claude-sonnet-4-6`, `claude-haiku-4-5`, plus an empty
  "Default (Claude recommended)" entry.
  (Fable — `claude-fable-5` — was previously left unpinned as a product choice;
  Rob asked to add it on 2026-07-02. It needs Claude Code ≥ 2.1.170 and is not
  the upstream default; safety-flagged requests auto-fall-back to Opus.)
  `claude-fable-5-1` added 2026-09-01 — Fable 5.1 is the current Fable and the
  top entry. The point release is hyphenated in the ID: `claude-fable-5-1`, NOT
  `claude-fable-5.1`. `claude-fable-5` stays pinned below it — upstream lists it
  as "legacy (still available)", and the rule only removes a model once it is
  actually retired.
  `claude-opus-5` added 2026-07-24 — the current Opus, sitting between Fable and
  Opus 4.8 in the list. Fixed ID with no date suffix (same scheme as
  `claude-opus-4-8`); the `[1m]` suffix seen in Claude Code session banners is a
  context-window variant marker, NOT part of the `--model` value.
- Codex: `gpt-6-astra`, `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`,
  `gpt-5.5`, plus an empty "Default (Codex recommended)" entry. `gpt-6-astra`
  (Astra) was added as the top pinned entry on 2026-09-04. The official Codex
  models page documents the exact launch value as `codex -m gpt-6-astra`;
  launch announcement: https://openai.com/index/gpt-6-astra/. Live `codex debug models` on
  2026-08-23 (CLI 0.148.0) still lists `gpt-5.6-sol` / `terra` / `luna`,
  `gpt-5.5`, `gpt-5.4`, `gpt-5.4-mini`, and `gpt-5.3-codex-spark` as
  `"visibility": "list"`. This dropdown stays intentionally narrower: at the
  app owner's request, all choices below 5.5 were removed on 2026-07-10,
  including `gpt-5.4`, `gpt-5.4-mini`, and `gpt-5.3-codex-spark`. They remain
  usable as explicit legacy/custom values. Upstream: `gpt-5.4` / `gpt-5.4-mini`
  retire from ChatGPT-signed Codex on 2026-08-31. The old `gpt-5` → `gpt-5.4` alias rewrite was removed
  from both `normalizeCodexModel()` and
  `CodexLlmCliEnvironment.NormalizeModel()` so unpinned saved values pass
  through unchanged and render as `… (custom)`.
- Antigravity (agy): `Gemini 3.5 Flash (Medium)`, `Gemini 3.5 Flash (High)`,
  `Gemini 3.5 Flash (Low)`, `Gemini 3.1 Pro (Low)`, `Gemini 3.1 Pro (High)`,
  `Claude Sonnet 4.6 (Thinking)`, `Claude Opus 4.6 (Thinking)`, `GPT-OSS 120B (Medium)`,
  plus an empty "Default (Antigravity recommended)" entry. NOTE: agy's `--model` value
  is the full display string verbatim — spaces and parens included — e.g.
  `--model "Gemini 3.5 Flash (Low)"`, not a slug. (Re-verified 2026-07-02 against
  the codelabs reference + community write-ups — the catalog is unchanged since
  v1.0.7; `agy models` prints nothing in a non-TTY, so it can't be scripted.)
- Copilot: `claude-fable-5`, `claude-opus-5`, `claude-sonnet-5`,
  `claude-sonnet-4.6`, `claude-sonnet-4.5`, `claude-haiku-4.5`,
  `claude-opus-4.8`, `claude-opus-4.8-fast`, `claude-opus-4.7`,
  `claude-opus-4.6`, `claude-opus-4.5`, `gpt-5.6-sol`, `gpt-5.6-terra`,
  `gpt-5.6-luna`, `gpt-5.5`, `gpt-5.4`, `gpt-5.4-mini`, `gpt-5.4-nano`,
  `gpt-5.3-codex`, `gpt-5-mini`, `gemini-3.7-flash`, `gemini-3.6-flash`,
  `gemini-3.5-flash`, `gemini-3.1-pro`, `mai-code-1.1-flash`,
  `mai-code-1-flash`, `raptor-mini`, `kimi-k2.7-code`, `kimi-k3`, `grok-4.6`,
  `grok-4.5`, plus an empty "Default (auto)" entry (empty omits `--model`;
  `--model auto` is Copilot's explicit auto-selection value). Added 2026-08-23
  from the supported-models table: `claude-opus-5`, the GPT-5.6 trio,
  `gemini-3.6-flash`, `gemini-3.7-flash`, `mai-code-1.1-flash`,
  `kimi-k2.7-code`, `kimi-k3`, `grok-4.5`, `grok-4.6`. Dropped 2026-08-23:
  `gemini-3-flash`, `gemini-2.5-pro` — gone from the table. Earlier drops
  (2026-07-02): `claude-sonnet-4`, `claude-opus-4.6-fast` (deprecated in CLI
  v1.0.66, replaced by `claude-opus-4.8-fast`), `gpt-5.2-codex`, `gpt-5.2`,
  `gpt-5.1`, `gpt-4.1`. IMPORTANT: Copilot availability is plan/policy-gated, so
  "Model X is not available" at launch does NOT mean the ID is wrong. The pinned
  list follows the docs' supported-in-CLI table, not one account's entitlements.
- OpenCode: `anthropic/claude-opus-5`, `anthropic/claude-sonnet-5`,
  `anthropic/claude-opus-4-5`, `anthropic/claude-sonnet-4-5`,
  `openai/gpt-5.6`, `openai/gpt-5.5`, `openai/gpt-5.2`, `openai/gpt-5.1-codex`,
  `google/gemini-3-pro`, `zai/glm-5.2`, `zai-coding-plan/glm-5.3`, `xai/grok-4.6`,
  `opencode/gpt-5.1-codex` (Zen), plus an empty "Default (OpenCode recommended)" entry.
  OpenCode model IDs are `provider/model` (the format `--model` and `opencode models` use).
  Refresh via `opencode models` (optionally `--refresh` to update the cache from models.dev).
  Added 2026-07-16 alongside the OpenCode CLI integration; `zai/glm-5.2` added 2026-07-17;
  `xai/grok-4.6` added 2026-08-15; `zai-coding-plan/glm-5.3` added 2026-08-16;
  `anthropic/claude-opus-5`, `anthropic/claude-sonnet-5`, `openai/gpt-5.6`, and
  `openai/gpt-5.5` added 2026-08-23.
  Re-verified live via `opencode models` on 2026-08-23: `xai/grok-4.6` is still the Grok
  coding model; glm-5.3 still ships ONLY under `zai-coding-plan` (plain `zai` still tops
  out at `zai/glm-5.2`). Do not pin `zai/glm-5.3`.
  `zai/glm-5.2` and `zai-coding-plan/glm-5.3` are also exposed as OpenCode-backed first-class
  base CLIs (see the "GLM 5.2" and "GLM 5.3" sections). Grok 4.6 is a native `grok` CLI, not
  an OpenCode pin — OpenCode still lists `xai/grok-4.6` for its own sessions.

The rule:

- **Add** a model when it is released — e.g. when Opus 4.9 ships, add
  `claude-opus-4-9` as the top non-default entry. Both render helpers already keep
  any unknown saved value as a `… (custom)` option, so existing environments never
  break while the list catches up.
- **Remove** a model when it is retired upstream — e.g. if Haiku 4.5 is removed,
  delete `claude-haiku-4-5`. Per the compatibility policy above, do not add
  migration code; an environment still pinned to a removed model is already broken
  and the user can recreate it.

How to verify what's current:

- Claude: the model-config docs at
  https://code.claude.com/docs/en/model-config (full model IDs and which alias
  maps to which version).
- Codex: run `codex debug models` to print Codex's live model catalog as JSON, check
  https://developers.openai.com/codex/models for released Codex models and exact
  `codex -m` values, and use the Codex config reference at
  https://developers.openai.com/codex/config-reference.
  Filter on `"visibility": "list"` — hidden entries (e.g. `codex-auto-review`)
  are not user-selectable.
- Antigravity: run `agy models` — it prints the catalog, but as an INTERACTIVE picker
  with no `--json`/scriptable flag (it prints NOTHING in a non-TTY), so read the
  printed names in a real terminal and update the list by hand. Hands-on reference:
  https://codelabs.developers.google.com/antigravity-cli-hands-on
- Copilot: the supported-models table at
  https://docs.github.com/en/copilot/reference/ai-models/supported-models (CLI
  column) plus release notes at https://github.com/github/copilot-cli/releases.
  You can positively confirm an ID with
  `copilot -p "Reply with just: ok" --model <id>` (a reply proves the ID), but a
  "not available" error is inconclusive — it also fires for plan-gated models.
- OpenCode (including GLM 5.2 / GLM 5.3 / Grok 4.6): CLI flags at
  https://opencode.ai/docs/cli/, config at https://opencode.ai/docs/config/,
  model IDs at https://opencode.ai/docs/models/. Refresh the pinned list with
  `opencode models` (optionally `--refresh` to update the cache from models.dev).

## Initial Message placeholders

The Initial Message (one shared field under the CLI picker, stored in
`Environments.CustomPrompt`) supports `{{...}}` placeholders. Three families:

- **User variables** — `{{branch_name}}`, `{{path default="docs/runbook.md"}}`.
  A dashboard launch pops the fill-values modal (`prompt-template-modal.js`);
  headless paths ship them literally, as always.
- **Built-ins, auto-resolved at launch** — `{{datetime}}` (`2026-08-12 14:35`,
  local), `{{date}}`, `{{time}}`, `{{git_branch}}` (branch of the
  workspace-resolved working directory), `{{env_name}}`. Case-insensitive;
  `default=` is ignored on them; these names are reserved and never become
  fill-in fields.
- **Step output** — `{{step:<guid>}}`, inserted by the field's "Insert step
  output" picker. Runs the referenced step (any phase; "Only when referenced" =
  Phase 2 exists for exactly this) hidden and captured, and splices its output
  in — trimmed, capped at 4000 chars. Best-effort: deleted step →
  `(user deleted this step function)`; non-zero exit keeps the output and
  appends the exit note; timeout notes itself. The launch always continues.

Resolution happens exactly once per launch, in the process that owns the PTY
(`PromptPlaceholderService`; called from `TerminalRoutes` for web tabs and
`CliLoop` for spawned terminals). Spawning routes deliberately do NOT bake the
prompt into argv anymore — `{{step:...}}` runs a command, so a second
resolution pass would run it twice. The resolved text is also what UserInputs
seq-1 records, so the recording always matches what the CLI received.

## Current Managed Settings

This is the inventory VibeRails currently manages in the custom environment UI.
Use it as the baseline when checking upstream CLI docs for removed, renamed, or
newly useful options.

### Claude

UI-managed launch and prompt settings:

- Initial Message: stored in `CustomPrompt`; sent as the first prompt.
- Model: `--model`; pinned full IDs from `renderClaudeModelOptions()` (see Model
  Lists). Also persisted as the `model` key in `settings.json`.
- Effort: `--effort`; values `low`, `medium`, `high`, `xhigh`, `max`. Also
  persisted as `effortLevel` in `settings.json` — EXCEPT `max`, which upstream
  treats as session-only and rejects in settings files, so `max` rides on the
  `--effort` flag alone (`ClaudeLlmCliEnvironment.NormalizeSettingsEffort`
  strips it on read and write; the edit modal recovers it from `CustomArgs`).
  Available levels vary by model (e.g. Opus 4.6 / Sonnet 4.6 have no `xhigh`);
  the dropdown stays the full set and Claude ignores an unsupported level.
- Fast Mode: settings-file only — `"fastMode": true` in `settings.json` (same as
  the in-session `/fast`). There is no `--fast` launch flag, so this round-trips
  purely through the settings API, never `CustomArgs`. Opus-only (Claude switches
  to Opus when enabled); research preview, billed via usage credits. The
  `"fastMode"` key was re-verified current in the fast-mode docs 2026-07-02
  (it no longer appears in the settings-reference table, but the fast-mode page
  documents it explicitly). Upstream supports fast mode on Opus 4.8 and 4.7
  only; Opus 4.7 fast mode is deprecated and slated for removal 2026-07-24.
- No Session Persistence: `--no-session-persistence`.
- System Prompt: `--system-prompt`.
- YOLO Mode: `--dangerously-skip-permissions`. This is the only permission control.
- Bare Mode: `--bare`.
- Debug Mode: `--debug`.

Current settings-file fields managed through `ClaudeSettingsDto`:

- `effortLevel`
- `model`
- `fastMode`

Each is removed from `settings.json` when empty/off. The settings file is loaded
at launch via the injected `CLAUDE_CONFIG_DIR` env var, so a settings-only key
like `fastMode` takes effect even with no launch flag. Everything else above is
launch-only and stored in `CustomArgs`. Per the permissions policy, VibeRails
never reads, writes, or removes Claude's `permissions` block (`allow` / `deny` /
`defaultMode`), nor the admin-managed `fastModePerSessionOptIn` key.

### Codex

UI-managed launch and prompt settings:

- Starting Message: stored in `CustomPrompt`; sent as the first prompt.
- Model: `--model`; pinned values from `renderCodexModelOptions()` (see Model
  Lists for the maintained list and how to refresh it).
- Effort: `-c model_reasoning_effort=<level>`; values `minimal`, `low`,
  `medium`, `high`, `xhigh`, `max`, `ultra`. Codex rejects `max` for
  `gpt-5.5`, so the form disables that combination and normalizes an existing
  `gpt-5.5` + `max` selection to `xhigh`.
- Fast Mode: `-c service_tier=fast --enable fast_mode`.
- YOLO Mode: `--dangerously-bypass-approvals-and-sandbox`. This is the only
  permission control.
- No Alternate Screen: `--no-alt-screen`.

Current config-file fields managed through `CodexSettingsDto`:

- `model`
- `model_reasoning_effort`
- `service_tier = "fast"` plus `[features].fast_mode`
- `tui.alternate_screen = "never"`

YOLO and all other permission posture is launch-only (`CustomArgs`). Per the
permissions policy, VibeRails never reads, writes, or removes `approval_policy`,
`sandbox_mode`, or `model_provider`; any user-set values there are preserved.
Starting messages live in `CustomPrompt`. Legacy VibeRails alias keys still
stripped on save: `prompt`, `yolo`, `full_auto`, `no_alt_screen`, `oss`,
`ask_for_approval`, and `approval`.

### Antigravity (agy)

Antigravity is **launch-flag-only** — no settings file, so it is managed exactly
like Copilot (everything rides in `CustomArgs` / `CustomPrompt`). The flags below
are verified against `agy --help` (v1.1.7 on 2026-08-23), not third-party docs.

- Initial Message: stored in `CustomPrompt`; sent as
  `agy --prompt-interactive=<text>` (agy has no positional-prompt form — `--print`/`-p`
  is a one-shot, non-interactive mode that would exit the session).
- Model: `--model`; pinned dropdown from `renderAntigravityModelOptions()` (see Model
  Lists). The value is the full display string `agy models` prints, e.g.
  `--model "Gemini 3.5 Flash (Low)"` — spaces + parens are part of the value, and they
  round-trip safely because `ShellArgSanitizer` quotes each arg at emit.
- Sandbox Mode: `--sandbox` (run with terminal restrictions enabled).
- YOLO Mode: `--dangerously-skip-permissions` — auto-approves every tool
  permission request. This is the only permission control.
- Additional Arguments: preserved in `CustomArgs` for advanced agy flags not
  modeled by VibeRails (e.g. `--add-dir <dir>`, `--conversation <id>`). Live
  `agy --help` on v1.1.7 also lists `--agent`, `--effort` (`low|medium|high`),
  and `--mode` (`accept-edits`, `plan`); those stay additional-args only.

There is no `AntigravitySettingsDto`, no settings file, and no
`/api/v1/antigravity/settings` route: agy is launch-flag-only, and its
per-environment config-dir mechanism is not a documented/verifiable env var, so
per the compatibility policy VibeRails writes no config for it. Model IS a pinned UI dropdown
(`renderAntigravityModelOptions()`, see Model Lists) — its `--model` value is the full
display string `agy models` prints, e.g. `--model "Gemini 3.5 Flash (Low)"`.

### Copilot

Copilot has no settings-file integration today. Its managed behavior is stored
in `CustomArgs` and `CustomPrompt`.

UI-managed launch and prompt settings:

- Initial Message: stored in `CustomPrompt`; launched through
  `--interactive=<text>`.
- Mode: `--mode`; values `interactive`, `plan`, `autopilot`.
- Model: `--model`; pinned values from `renderCopilotModelOptions()` (see Model
  Lists — Copilot is now a maintained pinned list like the others). Empty means
  no `--model` flag; `auto` is Copilot's explicit auto-selection value.
  Availability is plan/policy-gated per account, so the list is a superset of
  what any one account can launch.
- Permissions: `--allow-all-tools` for tool-only auto-approval, or `--yolo`
  for all permissions (`--allow-all` equivalent).
- Don't Ask User: `--no-ask-user`.
- Additional Arguments: preserved in `CustomArgs` for advanced Copilot flags not
  modeled by VibeRails. Live Copilot CLI 1.0.71 also lists `--effort` /
  `--reasoning-effort` (`none|minimal|low|medium|high|xhigh|max`); that stays
  additional-args only.

Legacy flags still read:

- `--allow-all`, normalized to YOLO permissions.
- `--plan`, normalized to `--mode plan`.
- `--autopilot`, normalized to `--mode autopilot`.

### OpenCode

OpenCode is **launch-flag-only** (like Antigravity/Copilot): there is no `OpencodeSettingsDto`
and no `/api/v1/opencode/settings` route. All options ride in `CustomArgs` / `CustomPrompt`.
OpenCode's documented `OPENCODE_CONFIG_DIR` is an additive overlay: it is loaded after the
standard global and project configuration and therefore does not isolate an environment from
the user's global OpenCode config. VibeRails instead sets `XDG_CONFIG_HOME` to the environment
root. OpenCode then resolves its standard config, agents, commands, and plugins beneath the
existing `opencode/` subdirectory. Project-local config still applies by OpenCode design.
VibeRails does **not** write or manage the `opencode.json` config file — the schema is large and
merged from multiple locations, so per the compatibility policy only launch flags are managed.
Credentials are NOT isolated: VibeRails leaves `XDG_DATA_HOME` unchanged, so `opencode auth
login` continues to use the user's global OpenCode data directory (normally
`~/.local/share/opencode/auth.json`).

UI-managed launch and prompt settings (re-verified against https://opencode.ai/docs/cli/
and live `opencode --help` on 2026-08-23; `--pure` toggle added 2026-07-19):

- Initial Message: stored in `CustomPrompt`; sent as `--prompt=<text>`. The TUI treats a
  positional arg as the `[project]` path, not a prompt, so this flag is mandatory for prompts.
- Model: `--model`; pinned `provider/model` values from `renderOpencodeModelOptions()` (see
  Model Lists). Empty means OpenCode's default.
- Agent: `--agent <name>`; free text (built-ins like `build`/`plan` or a custom agent).
- YOLO Mode: `--auto` — auto-approves permissions not explicitly denied. This is the only
  permission control.
- Run Without Plugins: `--pure` — runs OpenCode without loading external plugins. Useful for
  isolated/reproducible environments where third-party plugin behavior would otherwise leak in.
  Verified against `opencode --help` on 2026-07-19.
- Additional Arguments: preserved in `CustomArgs` for advanced opencode flags not modeled by
  VibeRails (e.g. `--continue`, `--session`, `--fork`, `--mini`). Live `opencode --help` on
  2026-08-23 also lists `--mini` (minimal interactive UI), `--no-replay`, and `--replay-limit`;
  those stay additional-args only — do not add first-class toggles unless a UI control fully
  owns them.

There is no `OpencodeSettingsDto`, no settings file, and no `/api/v1/opencode/settings` route.

MCP auto-registration is **not** wired for OpenCode: `opencode mcp add` is interactive (no
non-interactive flags), so the `cli mcp add viberails-mcp -- …` pattern used by the other CLIs
does not apply. OpenCode environments therefore do not get the VibeRails MCP stdio server
registered automatically. (Future option: write a minimal `opencode.json` `{ "mcp": {…} }` into
the env config dir — requires config-file management, deliberately deferred.)

### GLM 5.2 

GLM 5.2 is an **OpenCode-backed pseudo-CLI**: it launches `opencode` with `--model=zai/glm-5.2`
pinned. It exists as a first-class base CLI (dropdown entry in the terminal launcher) and as a
custom env type, so users get a dedicated entry instead of picking `zai/glm-5.2` from the
OpenCode model list every time.

Backend wiring (added 2026-07-19):

- Enum: `LLM.Glm52` (value 7). C# enum names can't contain hyphens/periods, so `LlmParser`
  special-cases the string `"glm-5.2"` → `LLM.Glm52` via a `SpecialCaseMap` dictionary.
- Executable: `opencode` (mapped in `CommandService.PrepareSessionAsync`, like `agy` for
  Antigravity). The enum name lowercased (`glm52`) is NOT the executable.
- Model injection: for **base CLI launches** (no envName), `CommandService` prepends
  `--model=zai/glm-5.2` to the launch args. For **custom env launches**, the model is already
  in `CustomArgs` (emitted by `buildOpencodeCustomArgs`), so no injection happens — this avoids
  a duplicate `--model` flag.
- Prompt convention: `--prompt=<text>` (same as OpenCode).
- Proxy: the Z.AI/GLM proxy (`OpenCodeLlmProxyLaunchEnabled`) applies to GLM 5.2 because it IS
  the `zai` provider model. See `CommandService.PrepareSessionAsync`.
- Env isolation: `XDG_CONFIG_HOME` (same as OpenCode).
- Launcher: `IOpencodeLlmCliLauncher` (reused from OpenCode).
- `getLlmName()` (frontend) maps `7` → `'GLM 5.2'`.

UI-managed launch and prompt settings (same as OpenCode, with Model pinned):

- Initial Message: stored in `CustomPrompt`; sent as `--prompt=<text>`.
- Model: **pinned to `zai/glm-5.2`** — the model field is a read-only display, not a dropdown.
  To use a different model, create a plain OpenCode env instead.
- Agent: `--agent <name>`; free text.
- YOLO Mode: `--auto`.
- Run Without Plugins: `--pure`.
- Additional Arguments: preserved in `CustomArgs`.

Frontend helpers (environment-controller.js) — shared by all three pseudo-CLIs:

- `isOpencodeBackedCli(cli)` returns true for `opencode`, `glm-5.2`, `glm-5.3` —
  use this instead of `=== 'opencode'` when routing to the OpenCode settings form / arg builder.
  Native Grok 4.6 is **not** OpenCode-backed (`isNativeGrokCli`).
- `pinnedModelForCli(cli)` returns `'zai/glm-5.2'` for `glm-5.2`,
  `'zai-coding-plan/glm-5.3'` for `glm-5.3`, and `null` for plain OpenCode.

### Grok 4.6

Grok 4.6 is a **native Grok Build CLI**: it launches `grok` with `--model=grok-4.6` pinned.
It exists as a first-class base CLI (dropdown entry in the terminal launcher) and as a
custom env type. OpenCode can still offer `xai/grok-4.6` in its own model list — that is a
different CLI.

Backend wiring (native harness, 2026-08-23; replaces the 2026-08-15 OpenCode-backed slice):

- Enum: `LLM.Grok46` (value 8). C# enum names can't contain hyphens/periods, so `LlmParser`
  special-cases the string `"grok-4.6"` → `LLM.Grok46` via a `SpecialCaseMap` dictionary.
- Executable: `grok` (mapped in `CommandService.PrepareSessionAsync`, like `agy` for
  Antigravity). The enum name lowercased (`grok46`) is NOT the executable.
- Model injection: for **base CLI launches** (no envName), `CommandService` prepends
  `--model=grok-4.6` to the launch args. For **custom env launches**, the model is already
  in `CustomArgs` (emitted by `buildGrokCustomArgs` as `-m grok-4.6`), so no injection happens.
- Prompt convention: trailing positional (TUI first turn). Do **not** use `-p` / `--single`
  (headless; the process exits).
- Proxy: the OpenCode Token Saver flag (`OpenCodeLlmProxyLaunchEnabled`) also covers native
  Grok's use of `/llm/xai`. `GROK_CLI_CHAT_PROXY_BASE_URL` and `GROK_MODELS_BASE_URL` point
  at `{apiBase}/llm/xai/v1`. `viberails_session` / `viberails_tab` ride `env_http_headers`
  in `~/.grok/config.toml` (header names and env-var names only). Auth is `XAI_API_KEY` or
  `grok login`. Do **not** set `GROK_HOME`. Do **not** add a second `HttpListener`, a
  `/llm/grok` sidecar, or a middleware skip-list entry (rejected 2026-08-15; see `API_SEC.md`).
- Env isolation: none. Launch-flag-only.
- Launcher: `IGrokLlmCliLauncher` (`CliExecutable = "grok"`).
- MCP: `grok mcp remove viberails-mcp` then `grok mcp add --scope user viberails-mcp -- {vb} mcp`.
- `getLlmName()` (frontend) maps `8` → `'Grok 4.6'`.

UI-managed launch and prompt settings:

- Initial Message: stored in `CustomPrompt`; sent as a trailing positional.
- Model: **pinned to `grok-4.6`** — the model field is a read-only display, not a dropdown.
- Effort: `--effort` (canonical flag `--reasoning-effort`; both are parsed on load). Values
  `none`, `minimal`, `low`, `medium`, `high`, `xhigh`, `max`. Empty omits the flag so Grok's
  default applies (config.toml `[models].default_reasoning_effort` or the TUI `/effort`
  default — this is the thinking level shown in the Grok TUI). Launch-flag-only; VibeRails
  does not write `config.toml`. Per-model menu aliases (e.g. `deep`) are not pinned — an
  unknown saved value round-trips as a `(custom)` dropdown entry. Verified 2026-08-29 against
  live `grok --help` and the Grok user-guide (`14-headless-mode.md`). There is no separate
  `--thinking` flag.
- YOLO Mode: `--yolo`.
- Additional Arguments: preserved in `CustomArgs`.
- Leftover OpenCode flags (`--model=xai/grok-4.6`, `--auto`, `--pure`, `--agent`) are rewritten
  or dropped on the next env save.

### GLM 5.3

GLM 5.3 is an **OpenCode-backed pseudo-CLI**: it launches `opencode` with
`--model=zai-coding-plan/glm-5.3` pinned. It exists as a first-class base CLI (dropdown entry
in the terminal launcher) and as a custom env type, so users get a dedicated entry instead of
picking the model from the OpenCode model list every time.

**Provider note (the big difference from GLM 5.2):** glm-5.3 ships ONLY under the
`zai-coding-plan` provider (the Z.AI coding-plan subscription), NOT plain `zai`. Verified live
via `opencode models` on 2026-08-16 — the `zai` provider tops out at `zai/glm-5.2`. Do not
"fix" the pinned ID to `zai/glm-5.3`; that model does not exist in the catalog and the launch
would fail.

Backend wiring (added 2026-08-16):

- Enum: `LLM.Glm53` (value 9). C# enum names can't contain hyphens/periods, so `LlmParser`
  special-cases the string `"glm-5.3"` → `LLM.Glm53` via a `SpecialCaseMap` dictionary.
- Executable: `opencode` (mapped in `CommandService.PrepareSessionAsync`, like `agy` for
  Antigravity). The enum name lowercased (`glm53`) is NOT the executable.
- Model injection: for **base CLI launches** (no envName), `CommandService` prepends
  `--model=zai-coding-plan/glm-5.3` to the launch args. For **custom env launches**, the model
  is already in `CustomArgs` (emitted by `buildOpencodeCustomArgs`), so no injection happens —
  this avoids a duplicate `--model` flag.
- Prompt convention: `--prompt=<text>` (same as OpenCode).
- Proxy: the OpenCode Token Saver proxy config (`OPENCODE_CONFIG_CONTENT`) IS injected for
  GLM 5.3 (it is OpenCode-backed), but that config only remaps the `zai` and `xai` providers.
  The `zai-coding-plan` provider is deliberately NOT remapped, so GLM 5.3's own traffic goes
  direct to Z.AI and is NOT captured by the token saver. Do not add a `zai-coding-plan` remap
  without first verifying the coding-plan upstream base URL (decided 2026-08-16).
- Env isolation: `XDG_CONFIG_HOME` (same as OpenCode).
- Launcher: `IOpencodeLlmCliLauncher` (reused from OpenCode).
- `getLlmName()` (frontend) maps `9` → `'GLM 5.3'`.

UI-managed launch and prompt settings (same as OpenCode, with Model pinned):

- Initial Message: stored in `CustomPrompt`; sent as `--prompt=<text>`.
- Model: **pinned to `zai-coding-plan/glm-5.3`** — the model field is a read-only display, not
  a dropdown. To use a different model, create a plain OpenCode env instead.
- Agent: `--agent <name>`; free text.
- YOLO Mode: `--auto`.
- Run Without Plugins: `--pure`.
- Additional Arguments: preserved in `CustomArgs`.

## Mental Model

Custom environments have two layers:

1. `CustomArgs` and `CustomPrompt` live in the `Environments` table and apply
   to every launch path.
2. Per-CLI settings files exist for Claude and Codex only. Antigravity, Copilot, OpenCode,
   GLM 5.2, GLM 5.3, and Grok 4.6 are frontend-managed through `CustomArgs` and `CustomPrompt`.

The important rule: a visible control is not enough. Every CLI option must
round-trip through render, read, save, parse existing args, and launch.

GLM 5.2 and GLM 5.3 are **OpenCode-backed pseudo-CLIs**: they reuse OpenCode's settings
form, arg builder, env isolation (`XDG_CONFIG_HOME`), and launcher, but pin `--model` to a specific
provider/model. In the frontend, `isOpencodeBackedCli(cli)` routes them to the OpenCode branch;
in the backend, `CommandService.PrepareSession` maps the enum to `opencode` and injects the
pinned `--model` for base CLI launches. Native Grok 4.6 is a separate `grok` binary
(`isNativeGrokCli`).

The OpenCode Token Saver proxy remaps both the `zai` and `xai` providers via
`OPENCODE_CONFIG_CONTENT` (plain OpenCode + GLM 5.2 + GLM 5.3). GLM rides `/llm/zai` →
`api.z.ai`; OpenCode-xAI and native Grok ride `/llm/xai` → `api.x.ai`. Both are Kestrel routes on the main host.
**GLM 5.3 is the exception:** its pinned `zai-coding-plan` provider is not remapped, so its
traffic goes direct to Z.AI (no token-saver capture) — deliberate, see the GLM 5.3 section.
Auth stays in OpenCode's global `auth.json`. Do not add a second listener or a `/llm/grok`
sidecar (rejected 2026-08-15; see `API_SEC.md`).

## Environment Visibility (Hidden + AutomationWorker flags)

Each environment carries two UI-classification booleans. Neither is a CLI option — they never enter
`CustomArgs` or any CLI config file.

`Hidden` (DB column `Environments.Hidden`, default 0; round-tripped through
`EnvironmentResponse.Hidden` and `Create/UpdateEnvironmentRequest.Hidden`):

- The create/edit modal exposes it as a "Hide from launch pickers" switch (not rendered for
  Workers — see below). The launch pickers' "Customize LLM list" modal writes the same column
  through the preferences save.
- When true, the environment is filtered out of the LLM/terminal launch pickers: the
  preferences catalog resolves it `enabled: false`, and the legacy `buildLlmSelectionOptions` /
  `populateLlmSelectionSelect` path in `utils.js` (chat-history filter etc.) skips it unless
  `includeHidden: true`.
- A hidden environment is **still** listed in the Environments table (with an eye-slash badge), still
  launchable from there ("Launch in external terminal" / "Web Terminal"), and still usable by Automations.
- `populateLlmSelectionSelect` re-injects the currently-selected value's hidden environment so an
  existing reference is never silently cleared when the picker is rebuilt.

`AutomationWorker` (DB column `Environments.AutomationWorker`, default 0; create-only via
`CreateEnvironmentRequest.AutomationWorker` — there is no update-path field):

- Marks a "Worker": an environment created from the Automation editor's "Add Worker" flow, named
  after its automation (one-name rule — the shared modal renders no name row for it).
- Workers are excluded from the LLM-picker preferences catalog server-side
  (`LlmPickerPreferenceService.IsSupportedCustomEnvironment`), so they never appear in launch
  pickers or the "Customize LLM list" modal regardless of `Hidden`, and preference saves can never
  touch a Worker's `Hidden` value. `buildLlmSelectionOptions` skips them unconditionally too.
- The automation editor's Worker picker (`js/modules/pickers/worker-picker.js`) lists every Worker
  from `/api/v1/environments` — `hidden` has no effect there.
- The Environments table shows Workers with a robot badge instead of the eye-slash badge.
- Pre-flag environments referenced by existing automations are deliberately not backfilled; the
  Worker picker resolves such a selection by id so it keeps working.

## Workspace Mode (where an environment runs)

`WorkspaceMode` (DB column `Environments.WorkspaceMode`, default 0; round-tripped through
`EnvironmentResponse.WorkspaceMode` and `Create/UpdateEnvironmentRequest.WorkspaceMode`) decides
the working directory. It applies to Workers exactly as it does to Environments — a nightly
automation that clones fresh every run is the clearest case for it.

| Value | Meaning |
|:---:|---|
| 0 | Project directory (default, original behaviour) |
| 1 | Its own clone, created on first launch and reused |
| 2 | Git clone and start fresh each run |

Modes 1 and 2 are the same mechanism at different retention. Both create a row in `Sandboxes`
owned by the environment (`EnvironmentId`), reuse `SandboxService` for the clone, and surface
Diff / Merge / Push buttons on the environment's row in the Environments table. The Sandboxes card
renders only sandboxes with a NULL owner, so a released workspace reappears there automatically.

Things that will bite, and are intentional:

- **On the wire it is an `int`, not the enum.** An unrecognised value is a 400 from explicit
  validation (`TryParseWorkspaceMode`), never a 500 out of the deserializer.
- **Nullable on update.** A cached client that omits it leaves the stored mode alone — switching
  an environment into or out of a clone is never a side effect of saving something else. The
  frontend omits the field entirely outside a git repo for the same reason.
- **The clone is made on first launch, not at create time.** "Mode set, `workspaceSandboxId` null"
  is the normal not-yet-provisioned state, not an error.
- **Mode 2 clones the last commit only.** No uncommitted work, and no gitignored files — so no
  `.env` and no local config. That is what "fresh" means, and it is the sharpest edge of the
  feature.
- **`--depth 1`** (inherited from `SandboxService`): the clone has no history, so an agent running
  `git log` / `git blame` / `git diff main...HEAD` in there will not get what it expects.
- **Submodules are not cloned** — `SandboxService` does not pass `--recurse-submodules`.
- **Git hooks are not in a clone**, so Git Guard / VCA are not installed in the workspace.
- **Retention is 3** (`RunWorkspaceService.MaxRetainedPerRunWorkspaces`), but it is a *soft* cap.
  A workspace is kept past retention when it still has an open session in it
  (`HasOpenSessionUnderDirectoryAsync`), when it is younger than
  `RunWorkspaceService.MinimumPruneAge` (10 min — the gap between the clone finishing and the
  CLI's session row appearing), or when the in-use check itself fails. Deleting a live run's
  working tree is not a trade retention is allowed to make; Windows file locks would refuse it
  anyway, but Linux would happily unlink the directory out from under a running agent.
  Pruning only touches names `WorkspaceNameSlug.ForRun` produced **for that environment id**, so
  a persistent workspace, a hand-attached sandbox, and another environment's clones are all
  out of reach.
- **Names are slugged, not shared, and identity lives in the id.** Environment names allow
  spaces; sandbox names and git branch names do not. Slugging is lossy — "Nightly Review" and
  "Nightly-Review" produce identical text — and the workspace root is a flat global
  `sandboxes/{name}`, so every generated name carries `-e{environmentId}`. Run names add a
  timestamp *and* a random token, because a timestamp alone has one-second precision and a burst
  of automations starts inside the same second. `ForEnvironment` must stay deterministic: it is
  re-derived on every launch to find a persistent workspace, and if it drifted the clone would be
  re-made instead of reused.
- **A workspace cannot be deleted from the Sandboxes card.** `DELETE /api/v1/sandboxes/{id}`
  returns 409 for a sandbox with an owner, and the card does not render owned rows. Release it
  first by changing the workspace mode or deleting the environment.
- The environment credential directory (`~/.vibe_rails/envs/{name}`) is **never** cloned or
  refreshed. Workspace mode is about the code, not the CLI's auth.

`ProjectPath` (same migration batch) scopes an environment to the project it was created in.
**NULL means it predates scoping and stays visible everywhere** — no backfill, so nothing
disappears from a project where it was already in use.

Scoping is enforced at every door, not just the list, because the underlying lookups are all
global — the unique key is `(CustomName, LLM)` with no project in it, and automations persist a
bare environment id:

| Path | Where |
|---|---|
| List | `EnvironmentRoutes` GET `/environments` |
| Read / update / delete by name | `EnvironmentRoutes.IsVisibleHere` — answers **404**, not 403, so a name's existence elsewhere stays private |
| Launch (Env page *and* every Job/Worker run) | `EnvironmentLaunchService.ResolveEnvironmentAsync`, against the launch's project |
| Automation create/update | `JobService.ValidateCommonAsync`, against the automation's own `ProjectPath` |
| Picker catalog | `LlmPickerPreferenceService.GetScopedEnvironmentsAsync` |

All of them go through `ProjectPathComparer.IsVisibleIn`. As with Workers, an out-of-scope
environment is absent from the picker catalog entirely, so a preferences save can never rewrite
another project's `Hidden` values.

`ProjectPathComparer` folds case only on Windows and macOS. On Linux `/work/Foo` and `/work/foo`
are different directories and must not be treated as one project — do not "simplify" that back to
an unconditional `OrdinalIgnoreCase`.

## Launch Flow

These routes read saved environment args before starting a TUI:

- `VibeRails/Routes/TerminalRoutes.cs` for Web UI terminal starts.
- `VibeRails/Routes/CliLaunchRoutes.cs` for external terminal launches.
- `VibeRails/Routes/SandboxRoutes.cs` for sandbox launches.
- `VibeRails/Routes/TerminalRoutes.cs` bootstrap helpers for `vb --env`.

All paths validate and split `CustomArgs` with
`VibeRails/Utils/ShellArgSanitizer.cs`. Initial prompts are appended through
`VibeRails/Services/LlmClis/LlmPromptArgvBuilder.cs`.

## Main Files

Frontend form and launch args:

- `VibeRails/wwwroot/js/modules/environment-controller.js`

Environment create/update API:

- `VibeRails/Routes/EnvironmentRoutes.cs`
- `VibeRails/DTOs/ResponseRecords.cs`
- `VibeRails/DTOs/LLM_Environment.cs`

Settings APIs for CLIs that have config files:

- `VibeRails/Routes/LlmSettingsRoutes.cs`
- `VibeRails/DTOs/ClaudeSettingsDto.cs`
- `VibeRails/DTOs/CodexSettingsDto.cs`
- `VibeRails/Services/LlmClis/ClaudeLlmCliEnvironment.cs`
- `VibeRails/Services/LlmClis/CodexLlmCliEnvironment.cs`

Tests:

- `Tests/ClaudeSettingsTests.cs`
- `Tests/CodexSettingsTests.cs`
- `Tests/ShellArgSanitizerTests.cs` if changing accepted argument syntax.

## Frontend Checklist

For a new or changed CLI option, update the matching branch in
`environment-controller.js`.

1. Render the control in `buildCliSettingsHtml(cli, settings)`.
2. Read the control in `extractCliSettingsPayload(cli)`.
3. Emit the launch flags in `build<CLI>CustomArgs(settings)`.
4. Parse existing saved flags in `merge<CLI>SettingsFromCustomArgs(settings, customArgs)`.
5. Add or update normalizers/render helpers when the option has a finite value set.
6. Add interactions in `bindCliSettingsInteractions()` only when controls affect each other.

The managed CLIs hide the raw `Custom Arguments` field through
`usesManagedCustomArgs()`. Antigravity and Copilot expose an `Additional Arguments`
field to preserve flags not covered by first-class controls. If you add a new
first-class option for those CLIs, remove its flags from `additionalArgs` in the
merge function so the option is not emitted twice after save.

## Backend Checklist

Only add backend settings support when the option belongs in a CLI config file.
If the option is launch-only, `CustomArgs` is enough.

Before adding any settings-file option, apply two hard rules:

- Never manage a permission, approval, or sandbox key in a settings file. Those
  are YOLO-or-nothing launch flags in `CustomArgs` (see the permissions policy).
- Never write or remove a settings-file key unless a UI control fully owns its
  value. A key VibeRails writes/removes but the UI cannot populate will wipe the
  user's value on every save. If VibeRails does not fully own the key, leave it
  alone (preserve it like an unknown field).

For Claude or Codex settings-file options:

1. Add the property to the relevant settings DTO.
2. Read it in `GetSettings()`.
3. Write it in `SaveSettings()`.
4. Preserve unknown user-managed fields in the underlying settings file.
5. Remove old VibeRails-managed keys that are no longer current upstream keys.
6. Add or update tests for missing files, partial files, writes, and old-key removal.

For Copilot options:

- There is no `CopilotSettingsDto` or `/api/v1/copilot/settings/{envName}` route.
- Add controls in `environment-controller.js`.
- Store launch behavior in `CustomArgs`.
- Store the initial message in `CustomPrompt`.

## Per-CLI Notes

Claude:

- Settings file: `{envBasePath}/{envName}/claude/settings.json`.
- API endpoint prefix: `/api/v1/claude/settings/{envName}`.
- Current launch args come from `buildClaudeCustomArgs()`.
- Current settings payload comes from `ClaudeSettingsDto`.
- Empty/default values should usually remove keys from `settings.json`.
- `customPrompt` is populated from the Claude initial message.

Codex:

- Settings file: `{envBasePath}/{envName}/codex/config.toml`.
- API endpoint prefix: `/api/v1/codex/settings/{envName}`.
- Current launch args come from `buildCodexCustomArgs()`.
- Current settings payload comes from `CodexSettingsDto`.
- Keep TOML comments and unknown fields when writing.
- Remove old VibeRails-managed keys instead of adding compatibility read paths.
- `customPrompt` is populated from the Codex starting message.

Antigravity (agy):

- No settings file — launch-flag-only (like Copilot). There is no DTO and no
  `/api/v1/antigravity/settings` route.
- Executable is `agy` (not "antigravity"); the in-app PTY maps it in
  `CommandService.PrepareSession`, and the initial-prompt convention lives in
  `LlmPromptArgvBuilder` (`--prompt-interactive=<text>`).
- Current launch args come from `buildAntigravityCustomArgs()`; existing args are
  round-tripped by `mergeAntigravitySettingsFromCustomArgs()`.
- Sandbox is `--sandbox`; YOLO is `--dangerously-skip-permissions` (the only
  permission control). Model is `--model` via `renderAntigravityModelOptions()` (values
  are the full display strings with spaces/parens, e.g. `"Gemini 3.5 Flash (Low)"`).
- `customPrompt` is populated from the Antigravity initial message.

Copilot:

- No settings file integration exists today.
- Current launch args come from `buildCopilotCustomArgs()`.
- Current pseudo-settings are parsed from `CustomArgs` by
  `mergeCopilotSettingsFromCustomArgs()`.
- Initial messages launch via `--interactive=<text>` through
  `LlmPromptArgvBuilder`.

OpenCode:

- No settings file integration — launch-flag-only (like Copilot/Antigravity), but
  `XDG_CONFIG_HOME` points at the environment root for user-level
  config/agents/commands/plugins isolation. `OPENCODE_CONFIG_DIR` is not injected because it is
  only an additive overlay; `XDG_DATA_HOME` remains global for credentials.
- Executable is `opencode` (== enum name lowercased); no remap is needed in
  `CommandService.PrepareSession` (unlike `agy`).
- Initial messages launch via `--prompt=<text>` through `LlmPromptArgvBuilder` (NOT positional —
  a positional arg is the project path).
- Current launch args come from `buildOpencodeCustomArgs()`; existing args are round-tripped by
  `mergeOpencodeSettingsFromCustomArgs()`.
- YOLO is `--auto` (the only permission control). Model is `--model provider/model` via
  `renderOpencodeModelOptions()`. Agent is `--agent`. Run Without Plugins is `--pure`.
- `customPrompt` is populated from the OpenCode initial message.
- No MCP auto-registration (`opencode mcp add` is interactive).

GLM 5.2 :

- Pseudo-CLI backed by OpenCode. Enum `LLM.Glm52` (7); `LlmParser` special-cases `"glm-5.2"`.
- Executable is `opencode` (remapped in `CommandService.PrepareSession` — the enum name
  lowercased `glm52` is NOT the executable). `--model=zai/glm-5.2` is injected for base CLI
  launches; custom envs carry it in `CustomArgs`.
- Settings form reuses OpenCode's (`isOpencodeBackedCli()` routes `glm-5.2` to the OpenCode
  branch); the Model field is a read-only display pinned to `zai/glm-5.2`.
- The Z.AI proxy applies (GLM 5.2 IS the `zai` provider model).
- `customPrompt` is populated from the initial message.
- No MCP auto-registration (inherited from OpenCode).

Grok 4.6 :

- Native Grok Build CLI. Enum `LLM.Grok46` (8); `LlmParser` special-cases `"grok-4.6"`.
- Executable is `grok` (remapped in `CommandService.PrepareSession` — the enum name
  lowercased `grok46` is NOT the executable). `--model=grok-4.6` is injected for base CLI
  launches; custom envs carry `-m grok-4.6` in `CustomArgs`.
- Settings form is launch-flag-only (`isNativeGrokCli()`); the Model field is a read-only
  display pinned to `grok-4.6`. Effort is `--effort` (alias of `--reasoning-effort`; the TUI
  thinking level). YOLO is `--yolo`.
- Token Saver reuses `/llm/xai` on the main Kestrel host (gated by the OpenCode proxy
  flag). Auth is `XAI_API_KEY` / `grok login`. Do not set `GROK_HOME`. Do not reintroduce
  `GrokLoopbackBridge` or `/llm/grok`.
- `customPrompt` is populated from the initial message and sent as a trailing positional.
- MCP auto-registration: `grok mcp remove` + `grok mcp add --scope user`.

GLM 5.3 :

- Pseudo-CLI backed by OpenCode. Enum `LLM.Glm53` (9); `LlmParser` special-cases `"glm-5.3"`.
- Executable is `opencode` (remapped in `CommandService.PrepareSession` — the enum name
  lowercased `glm53` is NOT the executable). `--model=zai-coding-plan/glm-5.3` is injected for
  base CLI launches; custom envs carry it in `CustomArgs`.
- The model ID is `zai-coding-plan/glm-5.3`, NOT `zai/glm-5.3` — glm-5.3 exists only under the
  `zai-coding-plan` provider in the OpenCode catalog (verified 2026-08-16).
- Settings form reuses OpenCode's (`isOpencodeBackedCli()` routes `glm-5.3` to the OpenCode
  branch); the Model field is a read-only display pinned to `zai-coding-plan/glm-5.3`.
- The OpenCode Token Saver proxy config is injected (OpenCode-backed), but it does NOT remap
  `zai-coding-plan` — GLM 5.3's traffic goes direct to Z.AI and is not captured by the token
  saver. Deliberate; do not add a `zai-coding-plan` remap without verifying the coding-plan
  upstream base URL.
- `customPrompt` is populated from the initial message.
- No MCP auto-registration (inherited from OpenCode).

## Add A New Option

1. Verify the CLI supports the option and capture the exact flag spelling.
2. Decide whether it is launch-only or should persist into the CLI config file.
3. Add the UI control in `buildCliSettingsHtml()`.
4. Add the payload field in `extractCliSettingsPayload()`.
5. Add flag emission in `build<CLI>CustomArgs()`.
6. Add round-trip parsing in `merge<CLI>SettingsFromCustomArgs()`.
7. If needed, add DTO/service read/write support and settings tests.
8. Run a syntax check on `environment-controller.js`.
9. Build or run targeted tests.

Use existing helpers instead of custom string concatenation:

- `pushStringArg(args, flag, value)` for one flag/value pair.
- `pushListArg(args, flag, value)` for newline or whitespace list values.
- `parseArgString(value)` to parse saved args.
- `quoteCustomArg(value)` before joining custom args.
- CLI-specific normalizers for enum-like values.

## Change An Existing Option

1. Change the visible label or choices in `buildCliSettingsHtml()`.
2. Update normalizers for the current supported value set.
3. Update `merge<CLI>SettingsFromCustomArgs()` to recognize current flags.
4. Update `build<CLI>CustomArgs()` to emit only the new/current flag shape.
5. If settings files are involved, remove old managed keys on write and do not
   add read fallbacks for unsupported keys.
6. Update tests around old-key removal and generated output.

Example: when a CLI renames `--old-mode value` to `--mode value`, the merge
function should read `--mode` and the builder should emit only `--mode`.

## Delete An Option

1. Remove the UI control from `buildCliSettingsHtml()`.
2. Remove the payload field from `extractCliSettingsPayload()`.
3. Stop emitting the flag in `build<CLI>CustomArgs()`.
4. Drop old managed flags on the next save unless they remain valid current CLI
   flags.
5. Remove DTO properties and settings read/write only if VibeRails should no
   longer manage that config key.
6. Add a test for the intended migration behavior.

Be explicit about old saved environments. If the upstream TUI no longer supports
an option, do not build a compatibility path; users can delete and recreate the
broken environment.

## Validation

Minimum local checks:

```powershell
node --check VibeRails\wwwroot\js\modules\environment-controller.js
dotnet build --artifacts-path .codex-test-artifacts
```

Targeted tests when settings DTOs or services change:

```powershell
dotnet test Tests\Tests.csproj --artifacts-path .codex-test-artifacts --filter FullyQualifiedName~CodexSettingsTests
dotnet test Tests\Tests.csproj --artifacts-path .codex-test-artifacts --filter FullyQualifiedName~ClaudeSettingsTests
```

Manual smoke test:

1. Create or edit one custom environment for the affected CLI.
2. Save it.
3. Confirm the environment table shows the expected generated `Custom Args`.
4. Re-open the environment and confirm controls match the saved values.
5. Launch from Web UI and external terminal if the option affects launch args.
