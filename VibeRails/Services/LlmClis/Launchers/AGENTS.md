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

The `--env`, `--environment`, and `--lmbootstrap` flags are all aliases — they all trigger
LMBootstrap mode. The value is resolved smartly:
1. If it matches an LLM enum name (claude/codex/gemini, case-insensitive) → base CLI launch
2. Otherwise → custom environment name, looked up in DB via `FindEnvironmentByNameAsync()`

See [Cli/AGENTS.md](../../../Cli/AGENTS.md) for full details on how `--env` resolution works.

## Architecture

```
IBaseLlmCliLauncher (Interface)
    │
    ├── BaseLlmCliLauncher (Abstract base class)
    │       │
    │       ├── ClaudeLlmCliLauncher   → CLAUDE_CONFIG_DIR
    │       ├── CodexLlmCliLauncher    → CODEX_HOME
    │       └── GeminiLlmCliLauncher   → XDG_* (multiple env vars)
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

### GeminiLlmCliLauncher
- **Executable**: `gemini`
- **Config Env Vars**: Uses XDG Base Directory specification
  - `XDG_CONFIG_HOME` → `{envBasePath}/{envName}/gemini/config`
  - `XDG_DATA_HOME` → `{envBasePath}/{envName}/gemini/data`
  - `XDG_CACHE_HOME` → `{envBasePath}/{envName}/gemini/cache`
  - `XDG_STATE_HOME` → `{envBasePath}/{envName}/gemini/state`

## Gemini Settings Feature

### Business Logic

The Gemini CLI supports per-environment settings configuration. Settings are stored in `settings.json` within the environment's config directory.

**Settings File Location**:
```
{envBasePath}/{envName}/gemini/config/gemini/settings.json
```

**Supported Settings**:

| Setting | DTO Property | JSON Path | Type | Default | Description |
|---------|--------------|-----------|------|---------|-------------|
| Theme | `Theme` | `theme` | string | "Default" | UI theme (Default/Dark/Light) |
| Sandbox | `SandboxEnabled` | `sandbox.enabled` | bool | true | Containerized tool execution |
| Auto-Approve | `AutoApproveTools` | `tools.autoAccept` | bool | false | Skip confirmations for safe ops |
| Vim Mode | `VimMode` | `general.vimMode` | bool | false | Vim keybindings |
| Updates | `CheckForUpdates` | `checkForUpdates` | bool | true | Auto-update checking |
| YOLO Mode | `YoloMode` | `security.disableYoloMode` (inverted) | bool | false | Auto-approve everything |

**JSON Mapping**:
```json
{
  "theme": "Default",
  "checkForUpdates": true,
  "general": {
    "vimMode": false
  },
  "sandbox": {
    "enabled": true
  },
  "tools": {
    "autoAccept": false
  },
  "security": {
    "disableYoloMode": true
  }
}
```

Note: `YoloMode` is inverted from `disableYoloMode` - when `YoloMode=true`, we set `disableYoloMode=false`.

### Technical Specs

#### DTO: `GeminiSettingsDto`
**File**: [DTOs/GeminiSettingsDto.cs](../../DTOs/GeminiSettingsDto.cs)

```csharp
public class GeminiSettingsDto
{
    public string Theme { get; set; } = "Default";
    public bool SandboxEnabled { get; set; } = true;
    public bool AutoApproveTools { get; set; } = false;
    public bool VimMode { get; set; } = false;
    public bool CheckForUpdates { get; set; } = true;
    public bool YoloMode { get; set; } = false;
}
```

#### Interface: `IGeminiLlmCliEnvironment`
**File**: [Interfaces/IGeminiLlmCliEnvironment.cs](../../Interfaces/IGeminiLlmCliEnvironment.cs)

```csharp
public interface IGeminiLlmCliEnvironment : IBaseLlmCliEnvironment
{
    Task<GeminiSettingsDto> GetSettings(string envName, CancellationToken cancellationToken);
    Task SaveSettings(string envName, GeminiSettingsDto settings, CancellationToken cancellationToken);
}
```

#### Service Implementation: `GeminiLlmCliEnvironment`
**File**: [GeminiLlmCliEnvironment.cs](../GeminiLlmCliEnvironment.cs)

Key methods:
- `GetSettings(envName)` - Reads `settings.json`, maps to DTO
- `SaveSettings(envName, dto)` - Merges DTO into existing JSON, preserves other fields
- `GetSettingsFilePath(envName)` - Resolves full path to `settings.json`

**Read Logic**:
1. Build path: `{envBasePath}/{envName}/gemini/config/gemini/settings.json`
2. If file doesn't exist, return default DTO
3. Parse JSON, extract values with null-coalescing defaults
4. Handle nested paths (e.g., `node["general"]?["vimMode"]`)

**Write Logic**:
1. Read existing JSON (or create new `JsonObject`)
2. Update only our managed fields
3. Create nested objects if missing (e.g., `node["sandbox"] ??= new JsonObject()`)
4. Serialize with `WriteIndented = true`
5. Write back to file

#### API Routes
**File**: [Routes.cs](../../Routes.cs)

```
GET  /api/v1/gemini/settings/{envName}  → GetGeminiSettings
PUT  /api/v1/gemini/settings/{envName}  → UpdateGeminiSettings
```

#### UI Integration
**File**: [wwwroot/js/modules/environment-controller.js](../../wwwroot/js/modules/environment-controller.js)

The `editEnvironment()` method:
1. Detects if environment is Gemini type
2. Fetches settings via API
3. Renders toggle switches and dropdown in modal
4. Saves both environment and Gemini settings on submit

## Adding New Settings

To add a new Gemini setting:

1. **Update DTO** - Add property to `GeminiSettingsDto`
2. **Update GetSettings()** - Add JSON path extraction
3. **Update SaveSettings()** - Add JSON path write
4. **Update UI** - Add form control in `editEnvironment()`

Example for adding a new boolean setting:
```csharp
// In GetSettings():
dto.NewSetting = node["section"]?["newSetting"]?.GetValue<bool>() ?? false;

// In SaveSettings():
node["section"] ??= new JsonObject();
node["section"]!["newSetting"] = settings.NewSetting;
```

## Testing

Unit tests are located in [Tests/GeminiSettingsTests.cs](../../../Tests/GeminiSettingsTests.cs).

Test coverage includes:
- Reading settings from valid JSON
- Reading with missing file (defaults)
- Reading with partial JSON (missing fields)
- Writing settings preserves existing fields
- Writing creates nested objects
- YOLO mode inversion logic

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
| Model | `Model` | `model` | string | "" | Override default model (e.g., o3, gpt-5-codex) |
| Sandbox | `Sandbox` | `sandbox` | string | "read-only" | Sandbox policy: read-only, workspace-write, danger-full-access |
| Approval | `Approval` | `approval` | string | "untrusted" | Approval mode: untrusted, on-failure, on-request, never |
| Full-Auto | `FullAuto` | `full_auto` | bool | false | Shortcut for approval=on-request + sandbox=workspace-write |
| Search | `Search` | `search` | bool | false | Enable web search capabilities |

**TOML Format**:
```toml
model = "o3"
sandbox = "workspace-write"
approval = "on-request"
full_auto = true
search = false
```

### Technical Specs

#### DTO: `CodexSettingsDto`
**File**: [DTOs/CodexSettingsDto.cs](../../DTOs/CodexSettingsDto.cs)

```csharp
public class CodexSettingsDto
{
    public string Model { get; set; } = "";
    public string Sandbox { get; set; } = "read-only";
    public string Approval { get; set; } = "untrusted";
    public bool FullAuto { get; set; } = false;
    public bool Search { get; set; } = false;
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

#### CLI Commands
**File**: [Cli/Commands/CodexCommands.cs](../../Cli/Commands/CodexCommands.cs)

```
vb codex settings <env>                    # Show all settings
vb codex get <env> --name <setting>        # Get specific setting
vb codex set <env> [options]               # Update settings
```

Options:
- `--model <value>`
- `--codex-sandbox <read-only|workspace-write|danger-full-access>`
- `--codex-approval <untrusted|on-failure|on-request|never>`
- `--full-auto` / `--no-full-auto`
- `--search` / `--no-search`

### Testing

Unit tests are located in [Tests/CodexSettingsTests.cs](../../../Tests/CodexSettingsTests.cs).

Test coverage includes:
- Reading settings from valid TOML
- Reading with missing file (defaults)
- Reading with partial TOML (missing fields)
- Handling quoted and unquoted values
- Writing settings preserves existing content
- Updating existing values
- Removing empty model field
- Adding new fields at end of file

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
| Model | `Model` | `model` | string | "" | Override default model (sonnet, opus, haiku, or full name) |
| Permission Mode | `PermissionMode` | `permissionMode` | string | "default" | Permission handling: default, plan, bypassPermissions |
| Allowed Tools | `AllowedTools` | `allowedTools` | string | "" | Comma-separated list of tools to auto-approve |
| Disallowed Tools | `DisallowedTools` | `disallowedTools` | string | "" | Comma-separated list of tools to disable |
| Skip Permissions | `SkipPermissions` | `skipPermissions` | bool | false | Skip all permission prompts (dangerous!) |
| Verbose | `Verbose` | `verbose` | bool | false | Enable verbose logging output |

**JSON Format**:
```json
{
  "model": "opus",
  "permissionMode": "plan",
  "allowedTools": "Read,Glob,Grep",
  "disallowedTools": "Bash",
  "skipPermissions": false,
  "verbose": true
}
```

### Technical Specs

#### DTO: `ClaudeSettingsDto`
**File**: [DTOs/ClaudeSettingsDto.cs](../../DTOs/ClaudeSettingsDto.cs)

```csharp
public class ClaudeSettingsDto
{
    public string Model { get; set; } = "";
    public string PermissionMode { get; set; } = "default";
    public string AllowedTools { get; set; } = "";
    public string DisallowedTools { get; set; } = "";
    public bool SkipPermissions { get; set; } = false;
    public bool Verbose { get; set; } = false;
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

#### CLI Commands
**File**: [Cli/Commands/ClaudeCommands.cs](../../Cli/Commands/ClaudeCommands.cs)

```
vb claude settings <env>                    # Show all settings
vb claude get <env> --name <setting>        # Get specific setting
vb claude set <env> [options]               # Update settings
```

Options:
- `--model <value>` - Model to use (sonnet, opus, haiku, or full name)
- `--permission-mode <value>` - Permission mode: default, plan, bypassPermissions
- `--allowed-tools <value>` - Comma-separated list of tools to auto-approve
- `--disallowed-tools <value>` - Comma-separated list of tools to disable
- `--skip-permissions` / `--no-skip-permissions` - Toggle skip permissions
- `--verbose` - Enable verbose logging

#### UI Integration
**File**: [wwwroot/js/modules/environment-controller.js](../../wwwroot/js/modules/environment-controller.js)

The `editEnvironment()` method:
1. Detects if environment is Claude type
2. Fetches settings via API
3. Renders model dropdown, permission mode selector, text inputs, and toggle switches
4. Saves both environment and Claude settings on submit

### Testing

Unit tests are located in [Tests/ClaudeSettingsTests.cs](../../../Tests/ClaudeSettingsTests.cs).

Test coverage includes:
- Reading settings from valid JSON
- Reading with missing file (defaults)
- Reading with partial JSON (missing fields)
- Handling empty JSON
- Handling boolean values
- Writing settings to JSON
- Preserving existing content
- Updating existing values
- Removing empty/default values
- Adding new fields to JSON
