<div align="center">
  <img src="https://raw.githubusercontent.com/robstokes857/vibe-rails/main/vscode-viberails/media/vs-logo.png" alt="VibeRails logo" width="100" height="100" />
  <h1>VibeRails</h1>
  <h3>An opinionated framework that keeps vibe coding from going off the rails</h3>
  <p>
    <a href="https://marketplace.visualstudio.com/items?itemName=viberails.vscode-viberails"><img alt="Marketplace version" src="https://badgen.net/vs-marketplace/v/viberails.vscode-viberails?label=marketplace&color=0078D4" /></a>
    <a href="https://marketplace.visualstudio.com/items?itemName=viberails.vscode-viberails"><img alt="Installs" src="https://badgen.net/vs-marketplace/i/viberails.vscode-viberails?color=0078D4" /></a>
    <a href="https://code.visualstudio.com/"><img alt="VS Code" src="https://img.shields.io/badge/VS%20Code-1.125%2B-007ACC" /></a>
    <a href="https://github.com/robstokes857/vibe-rails/blob/main/vscode-viberails/LICENSE"><img alt="License" src="https://img.shields.io/badge/license-MIT-blue.svg" /></a>
    <a href="https://viberails.ai/"><img alt="Website" src="https://img.shields.io/badge/web-viberails.ai-c084fc" /></a>
  </p>
</div>

VibeRails puts your AI coding CLIs in one VS Code panel. Run Claude Code, Codex, Grok, OpenCode,
GitHub Copilot and Antigravity side by side, plan their work on a Board, enforce your rules on
every commit, and search everything your agents have done.

![VibeRails running Claude Code, with Terminals, Board, Quality, Envs, Vibe AI, MCP, Automation and Settings in the top navigation](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/docs/images/terminals.png)

*Every screenshot here is VibeRails being used to build VibeRails.*

## Every coding CLI in one place

Launch any supported CLI in a real terminal tab. Sessions keep running when you switch pages, and
tabs reconnect when you come back. **View/Edit all LLMs** chooses which CLIs and saved
environments the launcher shows, and in what order, and can launch any of them.

![The View/Edit all LLMs dialog, with a visibility toggle, ordering arrows and a Launch button for each CLI](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/docs/images/all-llms.png)

## Replay any session, or hand it to another agent

Every session is saved to History. Replay it at up to 100× speed, download the raw session data,
or use **Send to…** to continue in another CLI or environment: VibeRails summarizes the session
and starts the new agent with that summary.

![A session's menu in the History rail, with Send to listing custom environments and base CLIs](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/docs/images/history-send-to.png)

![Replaying a recorded Claude Code session at 10× and then 100× speed](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/docs/images/session-replay.gif)

## A Board built for agents

Every card is a work item for an agent. Assign it to a CLI or environment and **Start work**
launches the agent with the card as its brief; agents post comments, handoffs and linked commits
back to the card over MCP. The robot button on each lane header attaches Automations, such as a
code review or a quality scan, that run when a card enters the lane.

![Board lanes with assigned cards, live agents and a running lane Automation](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/docs/images/board.gif)

## Rules and code quality

Rules live in `vc.rules.md` files in your repository, and Git Guard hooks enforce them on every
commit: `WARN` reports, `COMMIT` needs an acknowledgment in the commit message, and `STOP` blocks.
The code quality report grades the repository, ranks the files that need attention, and shows the
code as a live graph or as connected module cards. **Fix code quality with…** hands the report to
an agent.

![The code quality report: an animated code graph beside a B grade, the quality radar and ranked report files](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/docs/images/code-quality-graph.gif)

![The same report switched to connected module cards](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/docs/images/code-quality-cards.gif)

## Environments and Workers

Save a CLI with its model, arguments and initial prompt as a named environment, like Conda for
LLMs. Run it in the project folder, in its own git clone or in a fresh clone each time. Workers are
environments set aside for Automations, so unattended reviews always run with the same setup.

![Saved Codex, Claude and Grok code-review environments above the Workers used by Automations](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/docs/images/environments-workers.png)

## Vibe AI: search everything your agents have done

Sessions from every CLI are captured and indexed locally. Search them by meaning or keyword, and
let agents do the same through the `search_history` MCP tool, so a fix one CLI found is there for
the next.

![Vibe AI Search with a learn rate of 97, 1,670 captured sessions and coverage for each CLI](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/docs/images/vibe-ai-search.png)

## Built-in MCP server

VibeRails runs an MCP server inside the extension and registers it with every agent it launches.
Its tools cover Board cards, handoffs, reviews, session history, rule checks and TokenSaver. The
**MCP Explorer** shows each tool's description and input schema and calls it for you.

![The MCP Explorer inspecting the save_board_handoff tool, with its arguments and input schema](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/docs/images/mcp-explorer.png)

## Automations and TokenSaver

- **Automations** run repository scripts (`.py`, `.ps1`, `.sh`) and an optional Worker on demand,
  on a schedule, around commits, or when a card enters a Board lane. They run while VibeRails is
  open, with the same permissions as VibeRails, so review every Worker and script.
- **TokenSaver** trims noisy tool output, such as build logs, passing tests and repeated lines,
  before it reaches the model, for Claude Code, Codex, OpenCode and Grok. The **tokens saved**
  meter shows the running total.

## Supported CLIs

- Claude Code
- Codex
- Grok
- OpenCode, plus GLM 5.2, GLM 5.3, DeepSeek V4 Pro and Kimi K3 through OpenCode
- GitHub Copilot
- Antigravity (`agy`)
- A plain terminal

## Get Started

1. Install the extension from the VS Code Marketplace.
2. Click the **VibeRails** button in the status bar (bottom left), run `VibeRails: Open Dashboard`, or press `Ctrl+Alt+V` / `Cmd+Alt+V`.
3. Pick a CLI or saved environment on the **Terminals** page and start shipping.

You need at least one coding CLI installed and signed in. Install options for Windows, Linux, and Mac are available at https://viberails.ai/.

### Connect your viberails.ai account

Open **Settings → General → Account**, select **Sign in**, and choose **Open sign-in page**.
Sign in through your browser; the code is submitted automatically. Check the computer and approve
the request. VibeRails saves the connection automatically. The API-key field also accepts a
key from your account if you prefer to paste one. While an account is connected, VibeRails
uploads completed sessions to it.

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

*Last checked: 2026-10-04 by Claude Code (claude-opus-5-5)*
