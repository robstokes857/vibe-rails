# Uses

This describes the entire custom env option workflow but it has mainly been used for adding new models to the LLM/CLI provider

# Custom Environment CLI Options

This runbook explains how to change, add, or delete custom environment options
for the managed TUI CLIs: Claude, Codex, Antigravity, Copilot, OpenCode, and the
the OpenCode-backed pseudo-CLI GLM 5.2.

Use it when editing the Environments page's create/edit modal, the per-CLI
settings APIs, or the generated launch arguments stored in `CustomArgs`.

## CLI References

Use these upstream CLI references to verify exact option names, accepted values,
and launch behavior before changing generated arguments:

- Claude: https://code.claude.com/docs/en/cli-reference
- Claude settings: https://code.claude.com/docs/en/settings
- Codex: https://developers.openai.com/codex/cli/reference
- Codex config: https://developers.openai.com/codex/config-reference
- Antigravity (agy): https://antigravity.google/docs/cli-using (also `cli-getting-started`, `cli-settings`)
- Antigravity authoritative flags: run `agy --help` (and `agy models` for the live model catalog)
- Copilot: https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference
- OpenCode: https://opencode.ai/docs/cli/ (also `/docs/config/`, `/docs/models/`)

Status checked against the upstream references above on July 2, 2026. The Codex
catalog and pinned-list preference were refreshed from the live model picker on
this host on July 10, 2026. agy flags were re-verified against `agy --help` on
this host (unchanged since v1.0.8); the third-party write-ups still conflict
with each other, so trust `--help`. OpenCode CLI/config/models were verified
against the upstream docs on July 16, 2026.

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

Current pinned values (Claude refreshed 2026-07-24; Codex 2026-07-10; all others
2026-07-02):

- Claude (full model IDs): `claude-fable-5`, `claude-opus-5`, `claude-opus-4-8`,
  `claude-opus-4-7`, `claude-sonnet-5`, `claude-sonnet-4-6`,
  `claude-haiku-4-5`, plus an empty "Default (Claude recommended)" entry.
  (Fable — `claude-fable-5` — was previously left unpinned as a product choice;
  Rob asked to add it on 2026-07-02. It needs Claude Code ≥ 2.1.170 and is not
  the upstream default; safety-flagged requests auto-fall-back to Opus.)
  `claude-opus-5` added 2026-07-24 — the current Opus, sitting between Fable and
  Opus 4.8 in the list. Fixed ID with no date suffix (same scheme as
  `claude-opus-4-8`); the `[1m]` suffix seen in Claude Code session banners is a
  context-window variant marker, NOT part of the `--model` value.
- Codex: `gpt-5.6-sol`, `gpt-5.6-terra`, `gpt-5.6-luna`, `gpt-5.5`, plus an
  empty "Default (Codex recommended)" entry. This is intentionally narrower
  than the live catalog: at the app owner's request, all choices below 5.5 were
  removed from the pinned dropdown on 2026-07-10, including `gpt-5.4`,
  `gpt-5.4-mini`, and `gpt-5.3-codex-spark`. They remain usable as explicit
  legacy/custom values. The old `gpt-5` → `gpt-5.4` alias rewrite was removed
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
- Copilot: `claude-fable-5`, `claude-sonnet-5`, `claude-sonnet-4.6`,
  `claude-sonnet-4.5`, `claude-haiku-4.5`, `claude-opus-4.8`,
  `claude-opus-4.8-fast`, `claude-opus-4.7`, `claude-opus-4.6`,
  `claude-opus-4.5`, `gpt-5.5`, `gpt-5.4`, `gpt-5.4-mini`, `gpt-5.4-nano`,
  `gpt-5.3-codex`, `gpt-5-mini`, `gemini-3.5-flash`, `gemini-3.1-pro`,
  `gemini-3-flash`, `gemini-2.5-pro`, `mai-code-1-flash`,
  `raptor-mini`, plus an empty "Default (auto)" entry (empty omits `--model`;
  `--model auto` is Copilot's explicit auto-selection value). Dropped
  2026-07-02: `claude-sonnet-4`, `claude-opus-4.6-fast` (deprecated in CLI
  v1.0.66, replaced by `claude-opus-4.8-fast`), `gpt-5.2-codex`, `gpt-5.2`,
  `gpt-5.1`, `gpt-4.1` — none are in the current supported-models table.
  IMPORTANT: Copilot availability is plan/policy-gated, so "Model X is not
  available" at launch does NOT mean the ID is wrong (on this host even
  `claude-opus-4.7` is gated while `claude-sonnet-5` works). The pinned list
  follows the docs' supported-in-CLI table, not one account's entitlements.
- OpenCode: `anthropic/claude-opus-4-5`, `anthropic/claude-sonnet-4-5`,
  `openai/gpt-5.2`, `openai/gpt-5.1-codex`, `google/gemini-3-pro`,
  `zai/glm-5.2`, `opencode/gpt-5.1-codex` (Zen), plus an empty "Default (OpenCode recommended)" entry.
  OpenCode model IDs are `provider/model` (the format `--model` and `opencode models` use).
  Refresh via `opencode models` (optionally `--refresh` to update the cache from models.dev).
  Added 2026-07-16 alongside the OpenCode CLI integration; `zai/glm-5.2` added 2026-07-17;
  `zai/glm-5.2` is also exposed as a first-class base CLI and custom
  env type (see the "GLM 5.2" section below) — it launches `opencode` with the
  model pinned via `--model`, so users get a dedicated dropdown entry instead of picking the
  model from the OpenCode list.

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
- Codex: run `codex debug models` to print Codex's live model catalog as JSON, and
  the Codex config reference at https://developers.openai.com/codex/config-reference.
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
are verified against `agy --help` (v1.0.8), not third-party docs.

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
  modeled by VibeRails (e.g. `--add-dir <dir>`, `--conversation <id>`).

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
  modeled by VibeRails.

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

UI-managed launch and prompt settings (verified against https://opencode.ai/docs/cli/ on
2026-07-16; `--pure` toggle added 2026-07-19):

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
  VibeRails (e.g. `--continue`, `--session`, `--fork`).

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

Frontend helpers (environment-controller.js):

- `isOpencodeBackedCli(cli)` returns true for `opencode`, `glm-5.2` — use this
  instead of `=== 'opencode'` when routing to the OpenCode settings form / arg builder.
- `pinnedModelForCli(cli)` returns `'zai/glm-5.2'` for `glm-5.2` and `null` for plain OpenCode.

## Mental Model

Custom environments have two layers:

1. `CustomArgs` and `CustomPrompt` live in the `Environments` table and apply
   to every launch path.
2. Per-CLI settings files exist for Claude and Codex only. Antigravity, Copilot, OpenCode,
   and GLM 5.2 are frontend-managed through `CustomArgs` and `CustomPrompt`.

The important rule: a visible control is not enough. Every CLI option must
round-trip through render, read, save, parse existing args, and launch.

GLM 5.2 is an **OpenCode-backed pseudo-CLI**: it reuses OpenCode's settings form,
arg builder, env isolation (`XDG_CONFIG_HOME`), and launcher, but pins `--model` to a specific
provider/model. In the frontend, `isOpencodeBackedCli(cli)` routes it to the OpenCode branch;
in the backend, `CommandService.PrepareSession` maps the enum to `opencode` and injects the
pinned `--model` for base CLI launches.

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
