<div align="center">
  <img src="https://raw.githubusercontent.com/robstokes857/vibe-rails/main/vscode-viberails/media/vs-logo.png" alt="VibeRails logo" width="100" height="100" />
  <h1>VibeRails</h1>
  <h3>An opinionated framework that keeps vibe coding from going off the rails</h3>
  <p>
    <a href="https://marketplace.visualstudio.com/items?itemName=viberails.vscode-viberails"><img alt="Marketplace version" src="https://img.shields.io/visual-studio-marketplace/v/viberails.vscode-viberails?label=marketplace&color=0078D4" /></a>
    <a href="https://marketplace.visualstudio.com/items?itemName=viberails.vscode-viberails"><img alt="Installs" src="https://img.shields.io/visual-studio-marketplace/i/viberails.vscode-viberails?color=0078D4" /></a>
    <a href="https://code.visualstudio.com/"><img alt="VS Code" src="https://img.shields.io/badge/VS%20Code-1.85%2B-007ACC" /></a>
    <a href="https://github.com/robstokes857/vibe-rails/blob/main/vscode-viberails/LICENSE"><img alt="License" src="https://img.shields.io/badge/license-MIT-blue.svg" /></a>
    <a href="https://viberails.ai/"><img alt="Website" src="https://img.shields.io/badge/web-viberails.ai-c084fc" /></a>
  </p>
</div>

![VibeRails dashboard](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/vscode-viberails/media/1.png)

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
- VS Code

## See It in Action

### Environments

![VibeRails environments](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/vscode-viberails/media/2.png)

### Web UI Terminal

![VibeRails terminal demo 1](https://i.imgur.com/p86L0Ka.gif)

### Sandboxing and Multi-LLM Parallel Processing

![VibeRails terminal demo 2](https://i.imgur.com/is4r4Vp.gif)

## Get Started

1. Install the extension from the VS Code Marketplace.
2. Run `VibeRails: Open Dashboard` (or press `Ctrl+Alt+V` / `Cmd+Alt+V`).
3. Launch a base CLI or custom environment and start shipping.

Install options for Windows, Linux, and Mac are available at https://viberails.ai/.

## What's Bundled

The extension ships the whole VibeRails backend inside the VSIX — there is **no separate `vb` install and no runtime download**.

- A platform-specific native binary plus the dashboard assets, published per target: `win32-x64`, `linux-x64`, `darwin-x64`, `darwin-arm64`.
- Roughly 50–100 MB installed, depending on platform. You only ever download the build for your own platform.
- The backend binds a **dynamic port on the loopback interface only**. Nothing listens on an external address, and the dashboard talks to it over `localhost`.
- Every request is authenticated with a session and tab token minted at startup; the dashboard runs in a VS Code webview under a strict Content Security Policy.
- Agent configuration, session history, and crash dumps live under `~/.vibe_rails/`.

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

**A rule shows a different enforcement level than expected.** The dashboard's rule list and the commit-gating hook use different parsers: the dashboard reads only the `- rule text (LEVEL)` suffix form under a `## Vibe Rails Rules` heading, while commit gating also accepts the `- [LEVEL] rule text` prefix form anywhere in any `AGENTS.md`. Write rules in the suffix form under that heading to have both agree.

## Links

- Website: https://viberails.ai/
- GitHub: https://github.com/robstokes857/vibe-rails
- VS Code Extension: https://marketplace.visualstudio.com/items?itemName=viberails.vscode-viberails
- Issues: https://github.com/robstokes857/vibe-rails/issues

## License

[MIT](https://github.com/robstokes857/vibe-rails/blob/main/vscode-viberails/LICENSE)
