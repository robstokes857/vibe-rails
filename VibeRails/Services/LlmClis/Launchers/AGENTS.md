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

Only `--env` is supported for environment bootstrap mode. The value is resolved smartly:
1. If it matches an LLM enum name (claude/codex/antigravity, case-insensitive) → base CLI launch
2. Otherwise → custom environment name, looked up in DB via `FindEnvironmentByNameAsync()`

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
    │       └── CopilotLlmCliLauncher  → (none — launch-flag-only)
    │
    └── LaunchLLMService (Orchestrator - selects launcher by LLM type)
```

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

The Codex CLI supports per-environment settings configuration. Settings are stored in `config.toml` within the environment's config directory.

**Settings File Location**:
```
{envBasePath}/{envName}/codex/config.toml
```

**Supported Settings**:

| Setting | DTO Property | TOML Key | Type | Default | Description |
|---------|--------------|----------|------|---------|-------------|
| Ask For Approval | `AskForApproval` | `approval_policy` | string | "" | Approval mode: default, untrusted, on-request, never |
| YOLO | `Yolo` | `yolo` | bool | false | Bypass approvals and sandboxing |
| Full-Auto | `FullAuto` | `full_auto` | bool | false | Shortcut for low-friction local work |
| No Alternate Screen | `NoAltScreen` | `no_alt_screen` | bool | false | Disable alternate screen mode for the TUI |
| OSS Provider | `Oss` | `oss` | bool | false | Use the local open source model provider |
| Prompt | `Prompt` | `prompt` | string | "" | Optional text instruction to start the session |
| Model | `Model` | `model` | string | "" | Optional Codex model override |
| Effort | `Effort` | `model_reasoning_effort` | string | "" | Optional reasoning effort override |
| Fast Mode | `FastMode` | `service_tier` + `[features].fast_mode` | bool | false | Enables fast service tier for supported models |

**TOML Format**:
```toml
approval_policy = "on-request"
yolo = false
full_auto = true
no_alt_screen = true
oss = false
prompt = "Investigate failing tests"
model = "gpt-5.4"
model_reasoning_effort = "high"
service_tier = "fast"

[features]
fast_mode = true
```

### Technical Specs

#### DTO: `CodexSettingsDto`
**File**: [DTOs/CodexSettingsDto.cs](../../DTOs/CodexSettingsDto.cs)

```csharp
public class CodexSettingsDto
{
    public string AskForApproval { get; set; } = "";
    public bool Yolo { get; set; } = false;
    public bool FullAuto { get; set; } = false;
    public bool NoAltScreen { get; set; } = false;
    public bool Oss { get; set; } = false;
    public string Prompt { get; set; } = "";
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
    public bool FastMode { get; set; } = false;
}
```

#### Service Implementation: `CodexLlmCliEnvironment`
**File**: [CodexLlmCliEnvironment.cs](../CodexLlmCliEnvironment.cs)

Key methods:
- `GetSettings(envName)` - Reads `config.toml`, parses TOML format
- `SaveSettings(envName, dto)` - Updates TOML file, preserves comments and unknown fields
- `GetSettingsFilePath(envName)` - Resolves full path to `config.toml`

**TOML Parsing**:
Uses simple regex-based parsing for key = value format, supporting:
- Quoted strings: `key = "value"` or `key = 'value'`
- Unquoted strings: `key = value`
- Booleans: `key = true` or `key = false`

#### API Routes
**File**: [Routes.cs](../../Routes.cs)

```
GET  /api/v1/codex/settings/{envName}  → GetCodexSettings
PUT  /api/v1/codex/settings/{envName}  → UpdateCodexSettings
```

### Testing

Unit tests are located in [Tests/CodexSettingsTests.cs](../../../Tests/CodexSettingsTests.cs).

Test coverage includes:
- Reading settings from valid TOML
- Reading with missing file (defaults)
- Reading with partial TOML (missing fields)
- Normalizing legacy `approval = "on-failure"` to `on-request`
- Writing settings preserves existing content
- Removing unsupported legacy Codex options
- Removing empty prompt field

---

## Claude Settings Feature

### Business Logic

The Claude CLI supports per-environment settings configuration. Settings are stored in `settings.json` within the environment's config directory.

**Settings File Location**:
```
{envBasePath}/{envName}/claude/settings.json
```

**Supported Settings**:

| Setting | DTO Property | JSON Key | Type | Default | Description |
|---------|--------------|----------|------|---------|-------------|
| Effort | `Effort` | `effort` | string | "" | `--effort` value: low, medium, high, xhigh, max |
| No Session Persistence | `NoSessionPersistence` | `noSessionPersistence` | bool | false | `--no-session-persistence` |
| Permission Mode | `PermissionMode` | `permissionMode` | string | "default" | Permission handling: default, acceptEdits, plan, auto, dontAsk, bypassPermissions |
| System Prompt | `SystemPrompt` | `systemPrompt` | string | "" | `--system-prompt` text |
| Allow Dangerous Skip | `AllowDangerouslySkipPermissions` | `allowDangerouslySkipPermissions` | bool | false | `--allow-dangerously-skip-permissions` |
| Development Channels | `DangerouslyLoadDevelopmentChannels` | `dangerouslyLoadDevelopmentChannels` | string | "" | `--dangerously-load-development-channels` entries |
| Dangerously Skip Permissions | `DangerouslySkipPermissions` | `dangerouslySkipPermissions` | bool | false | `--dangerously-skip-permissions` |
| Allowed Tools | `AllowedTools` | `allowedTools` | string | "" | `--allowedTools` entries |
| Append System Prompt | `AppendSystemPrompt` | `appendSystemPrompt` | string | "" | `--append-system-prompt` text |
| Bare | `Bare` | `bare` | bool | false | `--bare` |
| Betas | `Betas` | `betas` | string | "" | `--betas` entries |
| Channels | `Channels` | `channels` | string | "" | `--channels` entries |
| Debug | `Debug` | `debug` | bool | false | `--debug` |
| Debug Filter | `DebugFilter` | `debugFilter` | string | "" | Optional `--debug` category filter |

**JSON Format**:
```json
{
  "effort": "high",
  "noSessionPersistence": true,
  "permissionMode": "plan",
  "systemPrompt": "You are a Python expert",
  "allowDangerouslySkipPermissions": true,
  "dangerouslyLoadDevelopmentChannels": "server:webhook",
  "dangerouslySkipPermissions": false,
  "allowedTools": "Bash(git log *)\nRead",
  "appendSystemPrompt": "Always use TypeScript",
  "bare": false,
  "betas": "interleaved-thinking",
  "channels": "plugin:my-notifier@my-marketplace",
  "debug": true,
  "debugFilter": "api,mcp"
}
```

### Technical Specs

#### DTO: `ClaudeSettingsDto`
**File**: [DTOs/ClaudeSettingsDto.cs](../../DTOs/ClaudeSettingsDto.cs)

```csharp
public class ClaudeSettingsDto
{
    public string Effort { get; set; } = "";
    public bool NoSessionPersistence { get; set; } = false;
    public string PermissionMode { get; set; } = "default";
    public string SystemPrompt { get; set; } = "";
    public bool AllowDangerouslySkipPermissions { get; set; } = false;
    public string DangerouslyLoadDevelopmentChannels { get; set; } = "";
    public bool DangerouslySkipPermissions { get; set; } = false;
    public string AllowedTools { get; set; } = "";
    public string AppendSystemPrompt { get; set; } = "";
    public bool Bare { get; set; } = false;
    public string Betas { get; set; } = "";
    public string Channels { get; set; } = "";
    public bool Debug { get; set; } = false;
    public string DebugFilter { get; set; } = "";
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
- `GetSettings(envName)` - Reads `settings.json`, maps to DTO
- `SaveSettings(envName, dto)` - Merges DTO into existing JSON, preserves other fields
- `GetSettingsFilePath(envName)` - Resolves full path to `settings.json`

**Read Logic**:
1. Build path: `{envBasePath}/{envName}/claude/settings.json`
2. If file doesn't exist, return default DTO
3. Parse JSON using `JsonNode`
4. Extract values with null-coalescing defaults

**Write Logic**:
1. Read existing JSON (or create new `JsonObject`)
2. Update only our managed fields
3. Remove fields when set to default/empty values
4. Serialize with `WriteIndented = true`
5. Write back to file

#### API Routes
**File**: [Routes.cs](../../Routes.cs)

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
- Reading with partial JSON (missing fields)
- Writing settings to JSON
- Preserving existing content
- Removing unsupported legacy fields
- Removing empty/default values
