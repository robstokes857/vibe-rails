# Custom Environment CLI Options

This runbook explains how to change, add, or delete custom environment options
for the four managed TUI CLIs: Claude, Codex, Gemini, and Copilot.

Use it when editing the Environments page's create/edit modal, the per-CLI
settings APIs, or the generated launch arguments stored in `CustomArgs`.

## CLI References

Use these upstream CLI references to verify exact option names, accepted values,
and launch behavior before changing generated arguments:

- Claude: https://code.claude.com/docs/en/cli-reference
- Claude settings: https://code.claude.com/docs/en/settings
- Codex: https://developers.openai.com/codex/cli/reference
- Codex config: https://developers.openai.com/codex/config-reference
- Gemini: https://geminicli.com/docs/cli/cli-reference/
- Gemini settings: https://geminicli.com/docs/reference/configuration/
- Copilot: https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference

Status checked against the upstream references above on June 4, 2026.

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
- Gemini: `general.defaultApprovalMode`.

Why this rule exists: a settings-file key that the UI cannot fully drive becomes
destructive. A previous build "managed" `permissions.allow` while no UI control
ever populated it, so every save deleted the user's curated allow list. Never
write or delete a settings-file key unless a UI control fully owns its value;
otherwise leave it alone (preserve it like any unknown field).

## Model Lists (hand-maintained — refresh these)

The Claude and Codex **Model** dropdowns are pinned, hand-curated lists. They do
not auto-discover models, so this runbook is the place that owns them: when an
upstream model ships or is retired, update the list here and in code. This is the
main reason this section exists — a future change ("Claude shipped 4.9", "Haiku
4.5 was removed") should land here.

Where they live in code
(`VibeRails/wwwroot/js/modules/environment-controller.js`):

- Claude: `renderClaudeModelOptions()`.
- Codex: `renderCodexModelOptions()`.

Current pinned values:

- Claude (full model IDs): `claude-fable-5`, `claude-opus-4-8`,
  `claude-opus-4-7`, `claude-sonnet-4-6`, `claude-haiku-4-5`, plus an empty
  "Default (Claude recommended)" entry. (Fable is the tier above Opus, so it
  sits at the top; its ID has no version-dot suffix pattern like the others.)
- Codex: `gpt-5.5`, `gpt-5.4`, `gpt-5.4-mini`, `gpt-5.3-codex`,
  `gpt-5.3-codex-spark`, `gpt-5.2`, plus an empty "Default (Codex recommended)"
  entry.

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
  persisted as `effortLevel` in `settings.json`.
- Fast Mode: settings-file only — `"fastMode": true` in `settings.json` (same as
  the in-session `/fast`). There is no `--fast` launch flag, so this round-trips
  purely through the settings API, never `CustomArgs`. Opus-only (Claude switches
  to Opus when enabled); research preview, billed via usage credits.
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
  `medium`, `high`, `xhigh`.
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

### Gemini

UI-managed launch and prompt settings:

- Initial Message: stored in `CustomPrompt`; sent as the first prompt.
- Sandbox Mode: `--sandbox` (settings file `tools.sandbox`).
- YOLO Mode: `--approval-mode yolo`; legacy `--yolo` is still read from saved
  args. This is the only permission control.
- Vim Mode: stored in settings file.
- Check for Updates: stored in settings file.
- Additional Arguments: preserved in `CustomArgs` for advanced Gemini flags not
  modeled by VibeRails (an unmodeled `--approval-mode auto_edit`/`plan` lands here).

Settings file fields persisted through `GeminiSettingsDto`:

- `general.vimMode`
- `general.enableAutoUpdate`
- `tools.sandbox`

`theme` is read (preserved) but not written. Per the permissions policy,
VibeRails never reads, writes, or removes `general.defaultApprovalMode`; YOLO is
launch-only.

Old fields intentionally not supported in settings files:

- `checkForUpdates`
- `sandbox.enabled`
- `tools.autoAccept`

Launch arg compatibility still handled by the frontend:

- `--yolo` / `-y` in saved launch args

### Copilot

Copilot has no settings-file integration today. Its managed behavior is stored
in `CustomArgs` and `CustomPrompt`.

UI-managed launch and prompt settings:

- Initial Message: stored in `CustomPrompt`; launched through
  `--interactive=<text>`.
- Mode: `--mode`; values `interactive`, `plan`, `autopilot`.
- Model: `--model`; current suggested values include Claude, GPT-5, GPT-4.1,
  and Codex model names from `renderCopilotModelOptions()`.
- Permissions: `--allow-all-tools` for tool-only auto-approval, or `--yolo`
  for all permissions (`--allow-all` equivalent).
- Don't Ask User: `--no-ask-user`.
- Additional Arguments: preserved in `CustomArgs` for advanced Copilot flags not
  modeled by VibeRails.

Legacy flags still read:

- `--allow-all`, normalized to YOLO permissions.
- `--plan`, normalized to `--mode plan`.
- `--autopilot`, normalized to `--mode autopilot`.

## Mental Model

Custom environments have two layers:

1. `CustomArgs` and `CustomPrompt` live in the `Environments` table and apply
   to every launch path.
2. Per-CLI settings files exist for Claude, Codex, and Gemini only. Copilot is
   currently frontend-managed through `CustomArgs` and `CustomPrompt`.

The important rule: a visible control is not enough. Every CLI option must
round-trip through render, read, save, parse existing args, and launch.

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
- `VibeRails/DTOs/GeminiSettingsDto.cs`
- `VibeRails/Services/LlmClis/ClaudeLlmCliEnvironment.cs`
- `VibeRails/Services/LlmClis/CodexLlmCliEnvironment.cs`
- `VibeRails/Services/LlmClis/GeminiLlmCliEnvironment.cs`

Tests:

- `Tests/ClaudeSettingsTests.cs`
- `Tests/CodexSettingsTests.cs`
- `Tests/GeminiSettingsTests.cs`
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
`usesManagedCustomArgs()`. Gemini and Copilot expose an `Additional Arguments`
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

For Claude, Codex, or Gemini settings-file options:

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

Gemini:

- Settings file: `{envBasePath}/{envName}/gemini/config/gemini/settings.json`.
- API endpoint prefix: `/api/v1/gemini/settings/{envName}`.
- Current launch args come from `buildGeminiCustomArgs()`.
- Current settings payload comes from `GeminiSettingsDto`.
- YOLO mode is a launch flag (`--approval-mode yolo`), not a persisted
  `security.disableYoloMode` setting. Legacy `--yolo` saved args are still
  parsed.
- `customPrompt` is populated from the Gemini initial message.

Copilot:

- No settings file integration exists today.
- Current launch args come from `buildCopilotCustomArgs()`.
- Current pseudo-settings are parsed from `CustomArgs` by
  `mergeCopilotSettingsFromCustomArgs()`.
- Initial messages launch via `--interactive=<text>` through
  `LlmPromptArgvBuilder`.

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
dotnet test Tests\Tests.csproj --artifacts-path .codex-test-artifacts --filter FullyQualifiedName~GeminiSettingsTests
dotnet test Tests\Tests.csproj --artifacts-path .codex-test-artifacts --filter FullyQualifiedName~CodexSettingsTests
dotnet test Tests\Tests.csproj --artifacts-path .codex-test-artifacts --filter FullyQualifiedName~ClaudeSettingsTests
```

Manual smoke test:

1. Create or edit one custom environment for the affected CLI.
2. Save it.
3. Confirm the environment table shows the expected generated `Custom Args`.
4. Re-open the environment and confirm controls match the saved values.
5. Launch from Web UI and external terminal if the option affects launch args.
