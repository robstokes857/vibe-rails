# CLI Commands

## Environment Launcher (--env)

The `--env` flag launches an LLM CLI with environment isolation. It's the core mechanism behind
both the Web UI terminal and the `vb launch` command.

### Aliases

`--env`, `--environment`, and `--lmbootstrap` all do the same thing. They set `IsLMBootstrap = true`
and store the value in `LMBootstrapCli`.

### Smart Resolution

The value passed to `--env` is resolved in this order:

1. **LLM enum match** (case-insensitive: claude, codex, gemini) — launch that CLI with default config
2. **Custom environment name** — look up in DB via `FindEnvironmentByNameAsync()`, find which CLI it's associated with, load its config

### Working Directory Resolution

`--workdir` is optional:
1. If `--workdir <path>` is provided — use that path
2. Else — detect git root from current directory (`git rev-parse --show-toplevel`)
3. If neither works — error with helpful message

### What LMBootstrap Does

When `vb --env <value>` runs, [LMBootstrap.cs](../LMBootstrap.cs) handles it:

1. Resolves the value to an LLM type + optional environment name
2. Resolves working directory (--workdir or git root)
3. If custom environment: sets isolated config env vars on its PTY
   - Claude: `CLAUDE_CONFIG_DIR` → `{envPath}/{envName}/claude`
   - Codex: `CODEX_HOME` → `{envPath}/{envName}/codex`
   - Gemini: `XDG_CONFIG_HOME`, `XDG_DATA_HOME`, etc. → `{envPath}/{envName}/gemini/*`
4. Creates a session in the database for logging
5. Launches the CLI executable (claude/codex/gemini) inside the PTY
6. Tracks user inputs and terminal output

### Examples

```
vb --env claude                         # Launch Claude, default config
vb --env "my-research-setup"            # Launch custom env (DB lookup finds CLI type)
vb --env gemini --workdir /path/to/proj # Launch Gemini in specific directory
vb --environment codex                  # Same as --env codex
```

### How It's Called

- **External terminal (Launch button):** `BaseLlmCliLauncher` builds the command and spawns a new terminal window
- **Web UI terminal:** Frontend calls `GET /api/v1/terminal/bootstrap-command` to get the command, sends it to the PTY shell
- **CLI directly:** User types `vb --env <value>` in their terminal

## Command Router

[CommandRouter.cs](CommandRouter.cs) routes CLI commands to their handlers. CLI commands are checked
**before** LMBootstrap mode, so `vb launch claude --env research` routes to `LaunchCommands` (not LMBootstrap).

### Command Priority

1. Known commands (`env`, `agent`, `rules`, `validate`, `hooks`, `launch`, `gemini`, `codex`, `claude`) — handled by `CommandRouter`
2. `--env` / `--lmbootstrap` flag — handled by `LMBootstrap`
3. No arguments — launches web server

## Files

| File | Purpose |
|------|---------|
| [CommandRouter.cs](CommandRouter.cs) | Routes CLI commands to handlers |
| [CliOutput.cs](CliOutput.cs) | Formatted console output helpers |
| [Commands/EnvCommands.cs](Commands/EnvCommands.cs) | `vb env` — environment CRUD |
| [Commands/LaunchCommands.cs](Commands/LaunchCommands.cs) | `vb launch` — launch CLIs in external terminal |
| [Commands/AgentCommands.cs](Commands/AgentCommands.cs) | `vb agent` — manage AGENTS.md files |
| [Commands/RulesCommands.cs](Commands/RulesCommands.cs) | `vb rules` — list validation rules |
| [Commands/ValidateCommands.cs](Commands/ValidateCommands.cs) | `vb validate` — run VCA validation |
| [Commands/HooksCommands.cs](Commands/HooksCommands.cs) | `vb hooks` — git hook management |
| [Commands/GeminiCommands.cs](Commands/GeminiCommands.cs) | `vb gemini` — Gemini CLI settings |
| [Commands/CodexCommands.cs](Commands/CodexCommands.cs) | `vb codex` — Codex CLI settings |
| [Commands/ClaudeCommands.cs](Commands/ClaudeCommands.cs) | `vb claude` — Claude CLI settings |

See also: [Services/Terminal/AGENTS.md](../Services/Terminal/AGENTS.md) for Web UI terminal details.
See also: [Services/LlmClis/Launchers/AGENTS.md](../Services/LlmClis/Launchers/AGENTS.md) for launcher internals.
