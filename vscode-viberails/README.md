<div align="center">
  <img src="https://raw.githubusercontent.com/robstokes857/vibe-rails/main/vscode-viberails/media/vs-logo.png" alt="VibeRails logo" width="100" height="100" />
  <h1>VibeRails</h1>
  <h3>An opinionated framework that keeps vibe coding from going off the rails</h3>
  <p>
    <a href="https://marketplace.visualstudio.com/items?itemName=viberails.vscode-viberails"><img alt="Marketplace version" src="https://img.shields.io/visual-studio-marketplace/v/viberails.vscode-viberails?label=marketplace&color=0078D4" /></a>
    <a href="https://marketplace.visualstudio.com/items?itemName=viberails.vscode-viberails"><img alt="Installs" src="https://img.shields.io/visual-studio-marketplace/i/viberails.vscode-viberails?color=0078D4" /></a>
    <a href="https://code.visualstudio.com/"><img alt="VS Code" src="https://img.shields.io/badge/VS%20Code-1.125%2B-007ACC" /></a>
    <a href="https://github.com/robstokes857/vibe-rails/blob/main/vscode-viberails/LICENSE"><img alt="License" src="https://img.shields.io/badge/license-MIT-blue.svg" /></a>
    <a href="https://viberails.ai/"><img alt="Website" src="https://img.shields.io/badge/web-viberails.ai-c084fc" /></a>
  </p>
</div>

![VibeRails Board with fictional work assigned to multiple coding agents](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/vscode-viberails/media/screenshots/15-board-wide.png)

*Screenshots show the shared VibeRails dashboard with fictional demo content. Agent activity, quality scores, and terminal output are illustrative.*

## AI Coding, With Guardrails

VibeRails is the control layer for AI coding inside VS Code. Work faster with Claude, Codex, Antigravity, Copilot, and OpenCode while keeping output consistent, reviewable, and secure.

## Key Features

- Environment Isolation: Like Conda for LLMs. Experiment with settings without breaking your primary setup.
- Cross-LLM Learning: Share context and learnings across Claude, Codex, Antigravity, Copilot, and OpenCode.
- RAG Without The Rot: Track repeated fixes, feature descriptions, and file changes so context stays useful.
- Few Shot Prompting: Get Antigravity or Codex to code like Claude, with up to 20% better performance.
- Rule Enforcement: Enforce standards before code gets pushed.
- Token Savings: Use smarter file hints to reduce token usage and cost.

## Secure by Design

- Secure local dashboard access.
- Isolated environment profiles for each workflow.
- Rule checks and session visibility help prevent risky changes from slipping through.

## Built for Multi-LLM Workflows

- Claude
- Codex
- Antigravity
- Copilot
- OpenCode
- GLM-5.2 (via OpenCode)
- Grok 4.6 (via OpenCode)
- VS Code

## See It in Action

### Board and lane agents

Keep requirements, agent discussion, and workflow in one place. Configure reviews and checks for cards entering a lane.

![Review lane with configured agents and checks](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/vscode-viberails/media/screenshots/03-lane-agents.png)

### Code quality and source relationships

Explore the code graph, switch to connected module cards, and inspect saved file measurements.

![Code graph and sample quality report](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/vscode-viberails/media/screenshots/06-code-graph.png)

![Connected module cards for the demo project](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/vscode-viberails/media/screenshots/07-code-cards.png)

### Environments and Workers

Reuse prompts and workspace settings across coding sessions and Automations.

![VibeRails coding environments and Automation Workers](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/vscode-viberails/media/screenshots/10-environments.png)

### Automations

Create repeatable workflows with scripts and an optional Worker. Automations run while VibeRails is open.

![Sample Automations and signed Python scripts](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/vscode-viberails/media/screenshots/11-automations.png)

### Web UI Terminal

Use the integrated terminal for CLI sessions. The example below contains a fictional transcript.

![VibeRails terminal with a sample agent session](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/vscode-viberails/media/screenshots/14-terminal.png)

[Browse the full screenshot collection](https://github.com/robstokes857/vibe-rails/blob/main/vscode-viberails/media/screenshots/README.md).

## Get Started

1. Install the extension from the VS Code Marketplace.
2. Click the **VibeRails** button in the status bar (bottom left), run `VibeRails: Open Dashboard`, or press `Ctrl+Alt+V` / `Cmd+Alt+V`.
3. Launch a base CLI or custom environment and start shipping.

Install options for Windows, Linux, and Mac are available at https://viberails.ai/.

### Connect your viberails.ai account

Open **Settings → General → Account**, select **Sign in**, and choose **Open sign-in page**.
Sign in through your browser; the code is submitted automatically. Check the computer and approve
the request. VibeRails saves the connection automatically. The API-key field also accepts a
key from your account if you prefer to paste one.

## What's Bundled

The extension ships the whole VibeRails backend inside the VSIX — there is **no separate `vb` install and no runtime download**.

- A platform-specific native binary plus the dashboard assets, published per target: `win32-x64`, `linux-x64`, `darwin-arm64`.
- Roughly 50–100 MB installed, depending on platform. You only ever download the build for your own platform.
- The backend binds a **dynamic port on the loopback interface only**. Nothing listens on an external address, and the dashboard talks to it over `localhost`.
- Every request is authenticated with a session and tab token minted at startup; the dashboard runs in a VS Code webview under a strict Content Security Policy.
- Environment configuration, session history, and crash dumps live under `~/.vibe_rails/`. (Rule files — `vc.rules.md` — live in the project repository, not under `~/.vibe_rails/`.)

## Settings

| Setting | Default | Description |
|---|---|---|
| `viberails.startupTimeoutMs` | `30000` | How long to wait for the backend to start before giving up. Raise it if a cold first launch on a slow machine times out. |

## Troubleshooting

**The dashboard won't open.** Open **View → Output** and pick the **VibeRails Backend** channel — startup failures, the detected port, and the backend's own stdout all land there. A window reload (`Developer: Reload Window`) clears most wedged states.

**"Bundled VibeRails backend is missing for &lt;target&gt;".** The VSIX for your platform didn't unpack correctly, or you installed a VSIX built for a different platform. Reinstall the extension from the Marketplace.

**Startup times out on first launch.** The first run of the native binary is the slowest (antivirus scanning, cold file cache). Raise `viberails.startupTimeoutMs`.

**Port already in use.** Ports are allocated dynamically per launch, so this normally resolves itself — run `VibeRails: Stop Dashboard`, then open it again. If a previous backend was force-killed and is still holding the port, end any stray `vb` process and retry.

**Windows SmartScreen / antivirus prompt.** The bundled `vb.exe` is a freshly built native binary, so it can trip reputation-based scanners on first run. Allowing it once is enough.

**The backend crashed.** Native crashes write a minidump to `~/.vibe_rails/crashdumps/`. Attach the dump and the **VibeRails Backend** output channel to a [GitHub issue](https://github.com/robstokes857/vibe-rails/issues).

**`Ctrl+Alt+V` does nothing.** On some keyboard layouts `Ctrl+Alt` acts as AltGr, and the Paste Image extension binds the same chord. Rebind `viberails.open` under **File → Preferences → Keyboard Shortcuts**.

**A rule isn't being enforced or shown.** The dashboard Rules page and the commit-gating hook share one parser (`AgentRuleSectionReader`), so they always agree on what a file declares. Rules must live under a `## Vibe Rails Rules` heading (legacy `## Vibe Control Rules` also accepted), and fenced code blocks are skipped — documentation that shows what a rule looks like is never enforced. Three line forms are accepted: `- [LEVEL] rule text`, `- rule text (LEVEL)`, and bare `- rule text` (defaults to `WARN`). A rule outside that heading, under a different heading such as `## Rules`, or inside a code fence is simply not read.

## Links

- Website: https://viberails.ai/
- GitHub: https://github.com/robstokes857/vibe-rails
- VS Code Extension: https://marketplace.visualstudio.com/items?itemName=viberails.vscode-viberails
- Issues: https://github.com/robstokes857/vibe-rails/issues

## License

[MIT](https://github.com/robstokes857/vibe-rails/blob/main/vscode-viberails/LICENSE)

---

*Last checked: 2026-08-06T18:21:17Z by opencode (glm-5.2)*
