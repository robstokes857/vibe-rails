# VibeRails

**VibeRails** is an opinionated framework that helps keep AI coding assistants from going off the rails.

**Live Site**: [https://viberails.ai/](https://viberails.ai/)

[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![TypeScript](https://img.shields.io/badge/TypeScript-7.0-3178C6)](https://www.typescriptlang.org/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

---

## Overview
- **Environment Isolation** - Like Conda for LLMs. Create separate environments to experiment with Claude, Codex, Antigravity, Copilot, or OpenCode settings without breaking your primary setup
- **Cross-LLM Learning** - Share context and learnings between different LLM providers (Claude, Codex, Antigravity, Copilot, and OpenCode)
- **RAG (Without The Rot) For Your Code** - Track things like repeated fixes the LLM forgets, including when you have to tell it the same thing 6 or 7 times in one session and it still doesn't understand, how you describe a feature and where that code lives, and file change summaries with commits, then only provide what’s useful at call time to prevent context rot.
- **Few Shot Prompting** - Get Antigravity or codex to code like Claude for code that has been done before with few shot prompting... Making them up to 20% better (research paper and eval data coming soon.)
- **Rule Enforcement** - Define and enforce coding standards like test coverage, cyclomatic complexity, logging practices, and more. LLMs fix their errors before code can be pushed or before the tech debt get astronomical.
- **Token Savings** - Learn your codebase and how you describe it, providing LLMs with smart file hints to reduce token usage and costs
- **Rule Files (`vc.rules.md`)** - Create and manage VibeRails rule files that gate every commit with WARN/COMMIT/STOP enforcement
- **Web Terminal** - Launch CLIs directly in the browser with xterm.js. Select base CLIs (Claude, Codex, Antigravity, Copilot, OpenCode, GLM 5.2, Grok 4.6) or custom environments from a visual dropdown with optgroups
- **Background Automations (Preview)** - Opt in to the per-user VibeRails Demon so scheduled Automations continue while the dashboard is closed. See the [VBD runbook](runbooks/VBD/VBD.md) for limits and recovery.

---

## Internal tools and feature logs

Click the VibeRails logo **three times within 900 ms** to open **Internal tools**.
The modal has an extensible tab bar:

- **About** shows the running application's version.
- **Data uploads** shows retained upload attempts, their latest outcome, and the session ID
  or database snapshot involved. Open a row's details or **View logs** to follow that attempt.
  Create, Edit, and Delete are visibly disabled placeholders for future data management.
- **Logs** opens with existing **Application logs**. Use **Source** to switch to **VibeRails
  Demon logs** or the new **Feature journal**. Filter by category/feature, level, or text,
  then refresh or page through the results. Feature events also support status and operation
  ID filters; an upload's **View logs** opens the journal for that specific attempt.

Both session sharing and the legacy full-database export record new attempts automatically.
`succeeded` means the upload completed; `failed`, `cancelled`, and `skipped` explain other
outcomes. `uploaded` with a warning means the server accepted a session but its local
confirmation could not be saved. An attempt left at `started` has no recorded final outcome
(for example, the process stopped); it is not proof that the server rejected the data.

Upload history starts with this version and only covers retained attempts. It does not backfill old
uploads or list every session waiting to be uploaded. An absent record does not establish
whether data was sent. The existing **Share session data** setting still controls uploading;
opening this modal or recording a local event never enables sharing.

### Logging a new feature

Inject `IFeatureLog` from `VibeRails.Services.Diagnostics` and explicitly write small,
structured events. Use a stable feature name and reuse one operation ID through the action:

```csharp
public sealed class FeatureXService(IFeatureLog featureLog)
{
    public void Run()
    {
        var operationId = Guid.NewGuid().ToString("N");
        featureLog.Write("feature-x", "started", "Feature X started.",
            operationId: operationId, subject: "Item 42", status: "started");

        // Perform the action; record failures/cancellation at the appropriate boundary too.

        featureLog.Write("feature-x", "completed", "Feature X completed.",
            operationId: operationId, subject: "Item 42", status: "succeeded");
    }
}
```

`level` defaults to `LogLevel.Information`; pass `LogLevel.Warning` or `LogLevel.Error` for
problems. New feature names automatically appear in the Logs filter once an event is available.
Use safe, concise messages and identifiers: never include API keys, authentication headers,
session content, remote response bodies, or raw exception messages. The upload integration
records only fixed event messages and identifiers. Select **Feature journal** to see these
events. Existing diagnostic files are also readable through the other Sources below; terminal
transcripts remain in Chat History.

### Viewing existing diagnostic logs

The application and Demon sources read the existing Serilog files directly from
`~/.vibe_rails/logs/vb-*.log` and `vbd-*.log`, including entries written before this UI existed.
They do not copy, rewrite, or migrate the files, and do not change the existing log writers.
The leading category tag, such as `[Jobs]`, `[Startup]`, or `[DataExport]`, populates the feature
filter; messages without a tag appear under `general`. Severity filters recognize the existing
three-letter log levels. Details show the source filename and multiline exception text.
Existing timestamps are local wall-clock time; the reader converts them to UTC for ordering
and the UI displays them in your browser's local time.

Each source reads at most the newest seven matching files, the last 2 MiB of each file, and
10,000 events. Directory enumeration is limited to 1,024 entries, and individual messages are
capped at 16,384 characters. The viewer indicates when these limits omit older data or part
of a message. Source snapshots are cached for two seconds and loaded only when requested;
there are no file watchers or background scans. Malformed or unreadable files produce a visible
read-error count. Partial lines are skipped until the writer completes them. Only the fixed log
sources are accepted, with links/reparse points excluded.

Historical diagnostic messages do not reliably identify an upload attempt or its final outcome,
so they remain searchable logs rather than being converted into upload-history rows.

### Storage and performance

Events are local JSON Lines files under `~/.vibe_rails/logs/features/` (beside the configured
`state.db` directory). The writer enqueues without waiting for disk, uses a bounded queue of
1,024 events, and writes up to 128 events per background batch, coalesced over 100 ms. Files
rotate at 2 MiB with a target of eight retained segments; active files belonging to other
running backends are preserved.
Each process has its own filenames so multiple dashboards can write concurrently.

Feature-journal reads happen on demand and are bounded to the newest eight segments and 10,000 events, with
a two-second cache and paged responses, so a refresh can briefly show the previous state. The
modal loads its module and tab data only when needed and does not poll in the background.
Terminal child processes use a no-op logger and have no new
logging worker. The internal read APIs are authenticated and available only on root backends:
`GET /api/v1/internal/logs` and `GET /api/v1/internal/uploads`.

Logging is best effort: queue overflow or disk errors never block an upload. The viewer reports
how many events the current process dropped or could not write (counted per event, for the
process lifetime) and how many files or records could not be read in the snapshot being shown
(counted per read, so refreshing over the same damaged file does not inflate it). Normal shutdown
drains the queue within the host's shutdown budget; an interrupted shutdown reports the events it
abandons as dropped, while a crash loses queued events silently. Rotation removes old history, so
this is a troubleshooting journal rather than a permanent audit archive.

## Jobs safety

> [!WARNING]
> Jobs are not sandboxed. They run unattended with the same operating-system permissions as VibeRails. “Review only” requests read-only behavior, and “Isolated write” uses a throwaway Git clone, but neither mode is a security boundary. An agent can access or modify files outside the selected repository or clone through CLI arguments, configuration, MCP servers, tools, or scripts. Review every environment and use a disposable account or machine for untrusted prompts, tools, or repositories.

---

## Status
- This repo is a lightweight, local-focused version of my personal setup. I'm stripping out multi-GPU/cluster support, heavy eval tooling, and other framework dependencies so it runs fast with Claude, Codex, and Antigravity CLIs. I'm rebuilding it around the features I think most people will actually want for local workflows.

## Quick Start

### Prerequisites

**For End Users:**
- Just install from VS Code Marketplace... That's it
- One or more LLM CLIs: Claude CLI, OpenAI Codex, Google Antigravity CLI (`agy`), GitHub Copilot, or OpenCode
- **No other dependencies required** - backend binaries are installed automatically on first run

**For Contributors (Working on the Project):**
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) or later (required to build the backend)
- [Node.js 24 LTS+](https://nodejs.org/) (required to build the VS Code extension; local development and release automation both read `.nvmrc`)
- Git
- VS Code 1.125.0 or later

### Installation

#### Build backend

```bash
# Clone the repository and its library submodules
git clone --recurse-submodules https://github.com/robstokes857/vibe-rails.git
cd vibe-rails

# Build and run
cd VibeRails
dotnet run
```

For an existing clone, initialize the submodules once with
`git submodule update --init --recursive`.

The dashboard will open in your default browser at `http://localhost:{port 5000-5999}`.

#### Option 2: VS Code Extension

```bash
# Navigate to extension directory
cd vscode-viberails

# Install dependencies
npm install

# Compile TypeScript
npm run compile

# Open in VS Code
code .
```

---

## Web Terminal Features

The integrated Web UI terminal lets you interact with LLM CLIs directly in your browser:

### Key Features
- Launch Claude, Codex, Antigravity, Copilot, or OpenCode CLIs in a browser-based terminal (xterm.js)
- Select from custom environments in a visual dropdown (Base CLIs vs Custom Environments)
- Auto-populate environment settings (custom args, prompts, model configs)
- Navigate from Environment Management → Dashboard with environment pre-selected
- Real-time CLI output with proper Unicode and color support
- PTY-backed sessions with full shell capabilities

### Usage

**Standard Launch:**
1. Go to Dashboard
2. Scroll to Terminal section
3. Select a CLI or custom environment from dropdown
4. Click "Start" to launch

**Quick Launch from Environments:**
1. Go to Environments page
2. Click "Web UI" button next to any environment
3. Dashboard opens with terminal section visible
4. Environment is pre-selected in dropdown
5. Click "Start" to launch with environment config applied

### Environment Integration

When you select a custom environment in the terminal:
- The backend automatically applies your custom args (e.g., `--verbose`, `--sandbox`)
- Your custom prompt is injected at session start
- Model-specific settings are configured (Claude model, Codex sandbox mode, etc.)
- The environment's "Last Used" timestamp is updated

This gives you quick access to different configurations without remembering command-line flags!

---

**Version**: 1.9.11
**Last Updated**: 2026-08-06
**Maintained By**: Robert Stokes

---

**Last checked**: 2026-08-06T18:21:17Z by opencode (glm-5.2)
