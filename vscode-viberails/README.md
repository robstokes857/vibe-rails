<div align="center">
  <img src="https://raw.githubusercontent.com/robstokes857/vibe-rails/main/vscode-viberails/media/vs-logo.png" alt="VibeRails logo" width="100" height="100" />
  <h1>VibeRails</h1>
  <p><strong>A developer and enterprise friendly framework that keeps vibe coding from
going off the rails</strong></p>
  <p>
    <a href="https://marketplace.visualstudio.com/items?itemName=viberails.vscode-viberails"><img alt="Marketplace version" src="https://badgen.net/vs-marketplace/v/viberails.vscode-viberails?label=marketplace&color=0078D4" /></a>
    <a href="https://marketplace.visualstudio.com/items?itemName=viberails.vscode-viberails"><img alt="Installs" src="https://badgen.net/vs-marketplace/i/viberails.vscode-viberails?color=0078D4" /></a>
    <a href="https://code.visualstudio.com/"><img alt="VS Code" src="https://img.shields.io/badge/VS%20Code-1.125%2B-007ACC" /></a>
    <a href="https://github.com/robstokes857/vibe-rails/blob/main/vscode-viberails/LICENSE"><img alt="License" src="https://img.shields.io/badge/license-MIT-blue.svg" /></a>
    <a href="https://viberails.ai/"><img alt="Website" src="https://img.shields.io/badge/web-viberails.ai-c084fc" /></a>
  </p>
</div>

Bring Claude Code, Codex, and other supported AI coding CLIs into a common development workflow
inside VS Code. VibeRails combines real terminals, repository standards, recorded sessions, and
human review with reusable agent setups and automation.

Developers keep their preferred coding tools. Teams get a consistent way to inspect work, apply
rules, connect issue trackers, and route provider traffic through corporate API gateways.
The extension bundles the VibeRails backend and dashboard and runs them on your machine.

![A Claude Code session running in the VibeRails terminal dashboard](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/docs/images/terminal-session.png)

## Built for developers and engineering teams

- **Repository standards:** versioned rules, Git Guard hooks, and code quality checks.
- **Review and traceability:** recorded sessions, replay, linked commits, and human approval of staged changes.
- **Team workflows:** Boards connected to Jira Cloud or Azure DevOps work items.
- **Reusable setups:** Quick Launches, Agents, and script/check Automations.
- **Corporate provider access:** configurable API gateways, including Codex and Claude gateway credentials.
- **Continuity across tools:** session handoffs, semantic history search, and built-in MCP tools.

## Repository standards and code quality

Define rules in `vc.rules.md` under a `## Vibe Rails Rules` heading. With Git Guard enabled,
installed Git hooks evaluate applicable rules for staged changes: **WARN** reports a finding,
**COMMIT** requires an acknowledgment and reason in the commit message, and **STOP** blocks a
commit when the rule fails. Developers and agents can also check rules from the dashboard or MCP.

![The Rules page showing Git Guard, repository rule files, and enforcement levels](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/docs/images/rules.png)

The MintLint code quality report scores supported source code and identifies files that need
attention. Explore the code graph or connected module cards, highlight changes, and use
**Fix code quality with…** to hand a report to an agent.

![The code graph with connected source modules and the repository quality report](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/docs/images/code-quality-graph.png)

![Connected module cards beside the code quality score and report files](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/docs/images/code-quality-cards.gif)

## Review and session traceability

Sessions launched through VibeRails are recorded for replay. Inspect the prompts, terminal
activity, and decisions behind a change; link sessions and commits to Board cards; and search
past work by meaning or keyword in **Vibe AI**.

**Review requests** let you send staged changes and their attached sessions to named reviewers
through viberails.ai before committing. Reviewers can inspect the work, comment, and approve
or reject. Signed approvals cover the exact staged tree and can satisfy configured repository
approval rules. The **Reviews requested of you** sidebar brings incoming requests into VS Code.

The dashboard's **Reviews** page also tracks GitHub pull request reviews across configured
repositories. **Send to…** summarizes a recorded session and starts another supported CLI or
saved setup with that context.

![The session history menu handing recorded work to another CLI or saved setup](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/docs/images/session-handoff.png)

## Boards and issue trackers

Plan work on a project Board, assign an agent, and retain comments, handoffs, commits, and
sessions with the card. Lane entry can trigger configured Agents and Automations for reviews
or checks. Connect **Jira Cloud** or **Azure DevOps** to pull work into VibeRails and send
comments and session references back to the linked issue or work item.

![A Jira-connected VibeRails Board with work organized into delivery and review lanes](https://raw.githubusercontent.com/robstokes857/vibe-rails/main/docs/images/jira-board.png)

## Agents, Quick Launches, and Automations

- **Quick Launches** save a CLI, model, arguments, and settings for reuse.
- **Agents** add a starting message and run on demand or on configured triggers.
- **Automations** run ordered scripts and checks without an LLM step, on demand, on a schedule,
  around commits, or when a card enters a Board lane.

Agent work can use the project folder, a separate clone, or a fresh clone for each run.
The signed scripts library supports PowerShell, Bash, and Python workflows. Agents and scripts
run with your operating-system account's permissions, including in separate clones; review
their prompts, tools, and scripts before enabling unattended work.

The Activity Bar's **Launch** view puts Agents, Automations, and scripts beside your code.
The backend starts quietly when VS Code opens a trusted workspace. Closing only the dashboard
tab leaves it running, with scheduled work available. **VibeRails: Stop**, the status-bar close
button, the dashboard's **Exit** button, or closing the VS Code window stops it. There is no
operating-system background service.

## Corporate gateways, TokenSaver, and MCP

Use **Settings → LLMs** to configure corporate API gateways for supported providers. Codex and
Claude support saved gateway credentials. The local relay forwards requests to a gateway that
accepts the provider's API format, so teams can use their existing provider access and network
controls. See the [gateway configuration guide](https://github.com/robstokes857/vibe-rails/blob/main/TokenSaver/README.md#enterprise-network-proxy).

**TokenSaver** can trim noisy tool output before it reaches Claude Code or Codex while preserving
user prompts and model replies. Savings depend on the workload; the terminal's **tokens saved**
meter shows the running total.

The built-in **MCP server** gives supported agents tools for Board work, handoffs, review tracking,
history search, rule validation, and TokenSaver control. **MCP Explorer** lets you inspect and
call those tools from the dashboard.

## Get started

1. Install VibeRails from the VS Code Marketplace and open a trusted workspace.
2. Click **VibeRails** in the status bar, run `VibeRails: Open Dashboard`, or press `Ctrl+Alt+V` / `Cmd+Alt+V`.
3. Launch an installed, authenticated AI CLI from **Terminals**, or create a saved setup.
4. Configure repository rules and enable **Git Guard** when you want commit checks.

Install and authenticate the AI CLI you want to use, such as Claude Code or Codex.
Standalone installers for Windows, Linux, and macOS are also available at [viberails.ai](https://viberails.ai/).

### Local operation and connected services

The VSIX includes a platform-specific native backend and dashboard assets for `win32-x64`,
`linux-x64`, or `darwin-arm64`. There is no separate VibeRails installation or backend download
at runtime. It listens on a dynamic loopback port, authenticates dashboard/API access, and stores
configuration and session history under `~/.vibe_rails/`. Repository rules stay with your code.
AI requests go to the configured provider or gateway.

To connect a **viberails.ai** account, open **Settings → General → Account**, select **Sign in**,
and approve the request in your browser. Connecting uploads completed sessions to that account.
Sending review requests, sharing sessions, and connecting issue trackers also transfer the
corresponding content to those services. Follow your team's data-handling policy when enabling
these features. The [API security contract](https://github.com/robstokes857/vibe-rails/blob/main/API_SEC.md)
documents the implemented boundaries.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `viberails.autoStart` | `true` | Start quietly when a trusted workspace opens. Disable to start only when you open the dashboard; use Stop to end a running backend. |
| `viberails.startupTimeoutMs` | `30000` | How long to wait for the backend to start. Raise it if a cold first launch times out. |

## Troubleshooting

**The dashboard won't open.** Open **View → Output** and select **VibeRails Backend** for startup
details. Run `VibeRails: Stop`, then reopen the dashboard, or try `Developer: Reload Window`.

**The bundled backend is missing.** Reinstall the Marketplace extension for your platform.

**Startup times out.** A cold launch or antivirus scan can take longer. Raise `viberails.startupTimeoutMs`.

**The keyboard shortcut does nothing.** Another extension or an AltGr keyboard layout may use
the same chord. Rebind `viberails.open` in **Keyboard Shortcuts**.

**A rule is not being read.** Rules must be inside a `## Vibe Rails Rules` section (the legacy
`## Vibe Control Rules` heading is also supported). Fenced examples are skipped. Accepted forms
are `- [LEVEL] rule text`, `- rule text (LEVEL)`, and `- rule text` (defaults to `WARN`).

## Links

- [Website and standalone installers](https://viberails.ai/)
- [Source code and project documentation](https://github.com/robstokes857/vibe-rails)
- [VS Code Marketplace](https://marketplace.visualstudio.com/items?itemName=viberails.vscode-viberails)
- [Report an issue](https://github.com/robstokes857/vibe-rails/issues)

## License

[MIT](https://github.com/robstokes857/vibe-rails/blob/main/vscode-viberails/LICENSE)

---

**Last updated** 2026-10-30
