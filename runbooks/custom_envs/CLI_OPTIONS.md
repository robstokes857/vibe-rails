# Custom Environment CLI Options

This runbook explains how to change, add, or delete custom environment options
for the four managed TUI CLIs: Claude, Codex, Gemini, and Copilot.

Use it when editing the Environments page's create/edit modal, the per-CLI
settings APIs, or the generated launch arguments stored in `CustomArgs`.

## CLI References

Use these upstream CLI references to verify exact option names, accepted values,
and launch behavior before changing generated arguments:

- Claude: https://code.claude.com/docs/en/cli-reference
- Codex: https://developers.openai.com/codex/cli/reference
- Gemini: https://geminicli.com/docs/reference/commands
- Copilot: https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-command-reference

## Current Managed Settings

This is the inventory VibeRails currently manages in the custom environment UI.
Use it as the baseline when checking upstream CLI docs for removed, renamed, or
newly useful options.

### Claude

UI-managed launch and prompt settings:

- Initial Message: stored in `CustomPrompt`; sent as the first prompt.
- Effort: `--effort`; values `low`, `medium`, `high`, `xhigh`, `max`.
- No Session Persistence: `--no-session-persistence`.
- Permission Mode: `--permission-mode`; values `acceptEdits`, `plan`, `auto`,
  `dontAsk`, `bypassPermissions`; default emits no flag.
- System Prompt: `--system-prompt`.
- Allow Dangerous Skip Permissions: `--allow-dangerously-skip-permissions`.
- Dangerously Skip Permissions: `--dangerously-skip-permissions`.
- Bare Mode: `--bare`.
- Debug Mode: `--debug`.

Settings file fields persisted through `ClaudeSettingsDto`:

- `effort`
- `noSessionPersistence`
- `permissionMode`
- `systemPrompt`
- `allowDangerouslySkipPermissions`
- `dangerouslyLoadDevelopmentChannels`
- `dangerouslySkipPermissions`
- `allowedTools`
- `appendSystemPrompt`
- `bare`
- `betas`
- `channels`
- `debug`
- `debugFilter`

### Codex

UI-managed launch and prompt settings:

- Starting Message: stored in `CustomPrompt`; sent as the first prompt.
- Model: `--model`; current suggested values are `gpt-5.5`, `gpt-5.4`,
  `gpt-5.4-mini`, `gpt-5.3-codex`, `gpt-5.3-codex-spark`, and `gpt-5.2`.
- Effort: `-c model_reasoning_effort=<level>`; values `minimal`, `low`,
  `medium`, `high`, `xhigh`.
- Ask For Approval: `--ask-for-approval`; values `on-request`, `untrusted`,
  `never`.
- Fast Mode: `-c service_tier=fast --enable fast_mode`.
- YOLO Mode: `--dangerously-bypass-approvals-and-sandbox`.
- Full-Auto Mode: `--sandbox workspace-write` plus
  `--ask-for-approval on-request`.
- No Alternate Screen: `--no-alt-screen`.

Settings file fields persisted through `CodexSettingsDto`:

- `approval_policy`
- `yolo`
- `full_auto`
- `no_alt_screen`
- `oss`
- `prompt`
- `model`
- `model_reasoning_effort`
- `service_tier = "fast"` plus `[features].fast_mode`

Legacy aliases still read:

- `ask_for_approval`
- `approval`
- `on-failure`, normalized to `on-request`
- `gpt-5`, normalized to `gpt-5.4`
- `gpt-5-codex`, normalized to `gpt-5.3-codex`

### Gemini

UI-managed launch and prompt settings:

- Initial Message: stored in `CustomPrompt`; sent as the first prompt.
- Sandbox Mode: `--sandbox`.
- YOLO Mode: `--yolo`.
- Approval Mode: `--approval-mode`; values `auto_edit`, `plan`; default emits
  no flag and YOLO disables the selector.
- Vim Mode: stored in settings file.
- Check for Updates: stored in settings file.
- Additional Arguments: preserved in `CustomArgs` for advanced Gemini flags not
  modeled by VibeRails.

Settings file fields persisted through `GeminiSettingsDto`:

- `theme`
- `general.vimMode`
- `general.enableAutoUpdate`
- `general.defaultApprovalMode`
- `tools.sandbox`

Legacy fields still read:

- `checkForUpdates`
- `sandbox.enabled`
- `tools.autoAccept`

### Copilot

Copilot has no settings-file integration today. Its managed behavior is stored
in `CustomArgs` and `CustomPrompt`.

UI-managed launch and prompt settings:

- Initial Message: stored in `CustomPrompt`; launched through
  `--interactive=<text>`.
- Mode: `--mode`; values `interactive`, `plan`, `autopilot`.
- Model: `--model`; current suggested values include Claude, GPT-5, GPT-4.1,
  and Codex model names from `renderCopilotModelOptions()`.
- Permissions: `--allow-all-tools` or `--yolo`.
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

For Claude, Codex, or Gemini settings-file options:

1. Add the property to the relevant settings DTO.
2. Read it in `GetSettings()`.
3. Write it in `SaveSettings()`.
4. Preserve unknown user-managed fields in the underlying settings file.
5. Add or update tests for missing files, partial files, writes, and legacy aliases.

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
- Remove legacy aliases when they can shadow the current key.
- `customPrompt` is populated from the Codex starting message.

Gemini:

- Settings file: `{envBasePath}/{envName}/gemini/config/gemini/settings.json`.
- API endpoint prefix: `/api/v1/gemini/settings/{envName}`.
- Current launch args come from `buildGeminiCustomArgs()`.
- Current settings payload comes from `GeminiSettingsDto`.
- YOLO mode is a launch flag (`--yolo`), not a persisted
  `security.disableYoloMode` setting.
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
2. Update normalizers so old saved values migrate to current values.
3. Update `merge<CLI>SettingsFromCustomArgs()` to recognize old flags.
4. Update `build<CLI>CustomArgs()` to emit only the new/current flag shape.
5. If settings files are involved, keep read fallbacks for old keys and remove
   old keys on write only when they can shadow the new key.
6. Update tests around legacy inputs and generated output.

Example: when a CLI renames `--old-mode value` to `--mode value`, the merge
function should still read `--old-mode` from existing environments, while the
builder should emit only `--mode`.

## Delete An Option

1. Remove the UI control from `buildCliSettingsHtml()`.
2. Remove the payload field from `extractCliSettingsPayload()`.
3. Stop emitting the flag in `build<CLI>CustomArgs()`.
4. Decide whether the merge function should preserve the old flag in
   `additionalArgs` or intentionally drop it on the next save.
5. Remove DTO properties and settings read/write only if VibeRails should no
   longer manage that config key.
6. Add a test for the intended migration behavior.

Be explicit about old saved environments. Silent deletion is acceptable only
when the old option is unsafe, unsupported, or intentionally retired.

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
