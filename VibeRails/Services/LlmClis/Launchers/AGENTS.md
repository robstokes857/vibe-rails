# LLM CLI Launchers

## Overview

This directory contains the CLI launcher implementations for different LLM providers. Each launcher is responsible for:
1. Defining the CLI executable name
2. Specifying environment variable configuration
3. Launching the CLI in platform-specific terminals

## Unified `--env` Flag

All launchers build commands using the unified `--env` flag:

- **Custom environment**: `vb --env "{envName}" --workdir "{dir}"`
- **Base CLI (no custom env)**: `vb --env {cliName} --workdir "{dir}"`

Only `--env` is supported for environment bootstrap mode. `ILlmParser.Parse()` does steps 1–2
and returns `LLM.NotSet` for anything else; the caller (`CliLoop.RunTerminalWithWebAsync`) then
performs step 3:
1. If it matches an LLM enum name (claude/codex/antigravity/copilot/shell/opencode, case-insensitive)
   → base CLI launch
2. The special-case strings `"glm-5.2"` and `"kimi-k3"` (can't be C# enum names) → OpenCode-backed
   pseudo-CLI base launch
3. Otherwise → custom environment name, looked up in DB via `FindEnvironmentByNameAsync()`

The old `--environment` / `--lmbootstrap` aliases and broader CLI command router have been removed.

## Architecture

```
IBaseLlmCliLauncher (Interface)
    │
    ├── BaseLlmCliLauncher (Abstract base class)
    │       │
    │       ├── ClaudeLlmCliLauncher   → CLAUDE_CONFIG_DIR
    │       ├── CodexLlmCliLauncher    → CODEX_HOME
    │       ├── AntigravityLlmCliLauncher → (none — launch-flag-only)
    │       ├── CopilotLlmCliLauncher  → (none — launch-flag-only)
    │       └── OpencodeLlmCliLauncher → XDG_CONFIG_HOME
    │
    └── LaunchLLMService (Orchestrator - selects launcher by LLM type)
```

> **Pseudo-CLIs:** `LLM.Glm52` and `LLM.KimiK3` (OpenCode launched with a pinned `--model` flag)
> reuse `IOpencodeLlmCliLauncher`. Their binary is `opencode` (mapped in
> `CommandService.PrepareSession`), and the model arg is injected server-side. `LLM.Shell` is a
> plain shell terminal with no launcher (handled specially in `CommandService.PrepareSession`).

## Launcher Implementations

### ClaudeLlmCliLauncher
- **Executable**: `claude`
- **Config Env Var**: `CLAUDE_CONFIG_DIR`
- **Config Path**: `{envBasePath}/{envName}/claude`

### CodexLlmCliLauncher
- **Executable**: `codex`
- **Config Env Var**: `CODEX_HOME`
- **Config Path**: `{envBasePath}/{envName}/codex`

### AntigravityLlmCliLauncher
- **Executable**: `agy` (note: the binary name differs from the product/enum name "Antigravity"; the in-app PTY maps it in `CommandService.PrepareSession`)
- **Config Env Var**: none — agy is launch-flag-only. There is no verified per-environment
  config-dir env var (the Node-era Gemini CLI used XDG; the Go-based agy exposes no documented
  equivalent), so `GetEnvironmentVariables` returns an empty dictionary, like Copilot.

### OpencodeLlmCliLauncher
- **Executable**: `opencode` (== enum name lowercased; no remap needed, unlike `agy`)
- **Config Env Var**: `XDG_CONFIG_HOME`, set to `{envBasePath}/{envName}`. OpenCode resolves its
  standard config/agents/commands/plugins directory beneath that root at `opencode/`.
  `OPENCODE_CONFIG_DIR` is intentionally not used because it is an additive overlay and still
  merges the user's global config. `XDG_DATA_HOME` is left unchanged, so credentials remain
  global. Launch-flag-only — no settings file is written (see
  runbooks/custom_envs/CLI_OPTIONS.md "### OpenCode"). YOLO is `--auto`; initial prompt is
  `--prompt=<text>` (positional = project path). VibeRails registers its MCP server via
  `opencode mcp add` (or `opencode.cmd mcp add` on Windows) as a setup command — see
  `CommandService.McpClis` / `GetMcpCommands`.

## Antigravity (agy) — no settings feature

Antigravity is **launch-flag-only** (like Copilot): there is no settings file, no
`AntigravitySettingsDto`, no `IAntigravityLlmCliEnvironment.GetSettings/SaveSettings`,
and no `/api/v1/antigravity/settings` route. All options are launch flags carried in
`CustomArgs` / `CustomPrompt`:

- Sandbox → `--sandbox`
- YOLO → `--dangerously-skip-permissions` (the only permission control)
- Initial message → `agy --prompt-interactive=<text>`
- Model → `--model <id>` (via Additional Arguments; not a UI dropdown)

`AntigravityLlmCliEnvironment.CreateEnvironment` only ensures the env subdirectory
exists. The frontend builds/parses args via `buildAntigravityCustomArgs()` /
`mergeAntigravitySettingsFromCustomArgs()`. Vim Mode and Check-for-Updates (old
Gemini settings-file features) were dropped: agy exposes no verified config-dir env
var, so per the compatibility policy VibeRails writes no config for it. Flags are
verified against `agy --help` (v1.0.8). The binary is `agy` even though the enum/
product name is "Antigravity" — see `CommandService.PrepareSession`.

---

## Codex Settings Feature

### Business Logic

The Codex CLI supports per-environment settings configuration. Settings are stored in `config.toml` within the environment's config directory. Permission posture (approval_policy / sandbox_mode) is YOLO-or-nothing via `CustomArgs` launch flags, so VibeRails neither reads nor edits those keys.

**Settings File Location**:
```
{envBasePath}/{envName}/codex/config.toml
```

**Supported Settings**:

| Setting | DTO Property | TOML Key(s) | Type | Default | Description |
|---------|--------------|-------------|------|---------|-------------|
| Model | `Model` | `model` | string | "" | Codex model override (e.g. gpt-5.6-sol) |
| Effort | `Effort` | `model_reasoning_effort` | string | "" | minimal/low/medium/high/xhigh/max/ultra |
| Fast Mode | `FastMode` | `service_tier` + `[features].fast_mode` | bool | false | Enables fast service tier for supported models |
| No Alternate Screen | `NoAltScreen` | `[tui].alternate_screen` | bool | false | Sets `alternate_screen = "never"` |
| YOLO | `Yolo` | (launch-only) | bool | false | Carried for the settings payload; persisted as `--dangerously-bypass-approvals-and-sandbox` in CustomArgs, never written to config.toml |

Legacy keys (`ask_for_approval`, `approval`, `yolo`, `full_auto`, `no_alt_screen`, `oss`, `prompt`)
are **removed** on save so saved environments use current Codex config names only.

**TOML Format**:
```toml
model = "gpt-5.6-sol"
model_reasoning_effort = "high"
service_tier = "fast"

[features]
fast_mode = true

[tui]
alternate_screen = "never"
```

### Technical Specs

#### DTO: `CodexSettingsDto`
**File**: [DTOs/CodexSettingsDto.cs](../../DTOs/CodexSettingsDto.cs)

```csharp
public class CodexSettingsDto
{
    // Fields persisted to config.toml.
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
    public bool FastMode { get; set; } = false;
    public bool NoAltScreen { get; set; } = false;

    // YOLO is launch-only (CustomArgs); never written to config.toml.
    public bool Yolo { get; set; } = false;
}
```

#### Service Implementation: `CodexLlmCliEnvironment`
**File**: [CodexLlmCliEnvironment.cs](../CodexLlmCliEnvironment.cs)

Key methods:
- `GetSettings(envName)` - Reads `config.toml`, parses TOML format
- `SaveSettings(envName, dto)` - Updates TOML file, preserves comments and unknown fields; strips legacy keys
- `GetSettingsFilePath(envName)` - Resolves full path to `config.toml`

**TOML Parsing**:
Uses simple regex-based parsing for key = value format, supporting:
- Quoted strings: `key = "value"` or `key = 'value'`
- Unquoted strings: `key = value`
- Booleans: `key = true` or `key = false`

#### API Routes
**File**: [Routes/LlmSettingsRoutes.cs](../../Routes/LlmSettingsRoutes.cs)

```
GET  /api/v1/codex/settings/{envName}  → GetCodexSettings
PUT  /api/v1/codex/settings/{envName}  → UpdateCodexSettings
```

### Testing

Unit tests are located in [Tests/CodexSettingsTests.cs](../../../Tests/CodexSettingsTests.cs).

Test coverage includes:
- Reading settings from valid TOML
- Reading with missing file (defaults)
- Writing settings preserves existing content
- Removing unsupported legacy Codex options (ask_for_approval, approval, yolo, full_auto, no_alt_screen, oss, prompt)

---

## Claude Settings Feature

### Business Logic

The Claude CLI supports per-environment settings configuration. Settings are stored in `settings.json` within the environment's config directory. Permission posture is YOLO-or-nothing: `DangerouslySkipPermissions` is the single YOLO toggle, carried as a launch flag in `CustomArgs`. VibeRails never reads or edits Claude's `permissions` block.

**Settings File Location**:
```
{envBasePath}/{envName}/claude/settings.json
```

**Supported Settings**:

| Setting | DTO Property | JSON Key | Type | Default | Persisted? | Description |
|---------|--------------|----------|------|---------|------------|-------------|
| Effort | `Effort` | `effortLevel` | string | "" | Yes | low/medium/high/xhigh (not "max" — session-only) |
| Model | `Model` | `model` | string | "" | Yes | Pinned model ID (e.g. claude-opus-4-8) |
| Fast Mode | `FastMode` | `fastMode` | bool | false | Yes | Same as `/fast`; Opus-only, no launch flag |
| Dangerously Skip Permissions | `DangerouslySkipPermissions` | — | bool | false | No (launch flag) | `--dangerously-skip-permissions` (YOLO) |
| No Session Persistence | `NoSessionPersistence` | — | bool | false | No (launch flag) | `--no-session-persistence` |
| System Prompt | `SystemPrompt` | — | string | "" | No (launch flag) | `--system-prompt` |
| Bare | `Bare` | — | bool | false | No (launch flag) | `--bare` |
| Debug | `Debug` | — | bool | false | No (launch flag) | `--debug` |

Stale top-level keys from older VibeRails builds (`effort`, `permissionMode`, `systemPrompt`,
`allowDangerouslySkipPermissions`, `dangerouslyLoadDevelopmentChannels`, `dangerouslySkipPermissions`,
`allowedTools`, `appendSystemPrompt`, `bare`, `betas`, `channels`, `debug`, `debugFilter`,
`skipPermissions`) are **removed** on save. The launch-only `noSessionPersistence` key is also
stripped (it is never persisted to `settings.json`). The user's `permissions` block is left untouched.

**JSON Format** (keys VibeRails writes):
```json
{
  "effortLevel": "high",
  "model": "claude-opus-4-8",
  "fastMode": true
}
```

### Technical Specs

#### DTO: `ClaudeSettingsDto`
**File**: [DTOs/ClaudeSettingsDto.cs](../../DTOs/ClaudeSettingsDto.cs)

```csharp
public class ClaudeSettingsDto
{
    // Fields persisted to settings.json.
    public string Effort { get; set; } = "";
    public string Model { get; set; } = "";
    public bool FastMode { get; set; } = false;

    // Launch-only flags carried in CustomArgs, NOT settings.json.
    public bool DangerouslySkipPermissions { get; set; } = false;
    public bool NoSessionPersistence { get; set; } = false;
    public string SystemPrompt { get; set; } = "";
    public bool Bare { get; set; } = false;
    public bool Debug { get; set; } = false;
}
```

#### Interface: `IClaudeLlmCliEnvironment`
**File**: [Interfaces/IClaudeLlmCliEnvironment.cs](../../Interfaces/IClaudeLlmCliEnvironment.cs)

```csharp
public interface IClaudeLlmCliEnvironment : IBaseLlmCliEnvironment
{
    Task<ClaudeSettingsDto> GetSettings(string envName, CancellationToken cancellationToken);
    Task SaveSettings(string envName, ClaudeSettingsDto settings, CancellationToken cancellationToken);
}
```

#### Service Implementation: `ClaudeLlmCliEnvironment`
**File**: [ClaudeLlmCliEnvironment.cs](../ClaudeLlmCliEnvironment.cs)

Key methods:
- `GetSettings(envName)` - Reads `settings.json`, maps `effortLevel`/`model`/`fastMode` to DTO
- `SaveSettings(envName, dto)` - Merges DTO into existing JSON, preserves other fields; strips stale top-level keys
- `GetSettingsFilePath(envName)` - Resolves full path to `settings.json`

**Read Logic**:
1. Build path: `{envBasePath}/{envName}/claude/settings.json`
2. If file doesn't exist, return default DTO
3. Parse JSON using `JsonNode`
4. Extract `effortLevel`, `model`, `fastMode` values

**Write Logic**:
1. Read existing JSON (or create new `JsonObject`)
2. Update only `effortLevel`, `model`, `fastMode`
3. Remove stale top-level keys from older VibeRails builds
4. Serialize with `WriteIndented = true`
5. Write back to file

#### API Routes
**File**: [Routes/LlmSettingsRoutes.cs](../../Routes/LlmSettingsRoutes.cs)

```
GET  /api/v1/claude/settings/{envName}  → GetClaudeSettings
PUT  /api/v1/claude/settings/{envName}  → UpdateClaudeSettings
```

#### UI Integration
**File**: [wwwroot/js/modules/environment-controller.js](../../wwwroot/js/modules/environment-controller.js)

The `editEnvironment()` method:
1. Detects if environment is Claude type
2. Fetches settings via API
3. Renders only the supported Claude flag controls listed above
4. Saves both environment and Claude settings on submit

### Testing

Unit tests are located in [Tests/ClaudeSettingsTests.cs](../../../Tests/ClaudeSettingsTests.cs).

Test coverage includes:
- Reading settings from valid JSON
- Reading with missing file (defaults)
- Writing settings to JSON
- Preserving existing content (including user permissions block)
- Removing stale top-level keys from older VibeRails builds
- Removing empty/default values
