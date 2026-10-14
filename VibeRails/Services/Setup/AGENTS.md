# Setup (prototype)

Settings → **Setup** lists what VibeRails needs on this machine and installs missing pieces.
Frontend: `wwwroot/js/modules/setup-panel.js` (panel, `buildSetupModel`, the app-load
warning bar) and `setup-install-window.js` (floating install terminal). Routes: `Routes/SetupRoutes.cs`,
root-only.

- `SetupToolCatalog` is the only source of install commands. Each method's `Command` is a
  compile-time constant taken from the vendor's own page; the first method is the recommended one.
  Keep commands in step with the linked pages. On Windows OpenCode is npm-only because
  `CommandService.ResolveOpenCodeMcpExecutable` calls npm's `opencode.cmd`.
- `SetupToolProbe` checks file existence only and never starts the tool. **Ready** means found
  on this process's PATH, which new terminals inherit. **Restart needed** means found only on the
  user/machine PATH Windows stores now, or at a vendor path. That is what a CLI installed after
  VibeRails started looks like. Versions come from `.exe` file metadata.
- `SetupStatusService` keeps one probe result in GlobalCache (`setup.tool-status.v1`). The app
  reads it on every load (beside the `.git` check) and shows a warning bar linking to Setup while
  a required tool or every AI CLI is missing. A complete result is reused until an install
  finishes; an incomplete one is probed again on each read, so a CLI installed elsewhere clears
  the bar. A saved result from another catalog version or OS is re-probed. Dismissing the bar
  lasts until the next load and is not stored.
- `SetupInstallTerminalSession` reuses `Services/Terminal/PrivatePtySession` (shared with the
  Scripts run window). The socket's first message names a tool ID and method ID only. The catalog
  text runs as a single `-Command`/`-c` argument of pwsh, else Windows PowerShell 5.1 (so pwsh can
  install itself), else the Unix login shell. The child gets the current stored PATH, so an npm
  install works right after Node.js was installed. See API_SEC.md, "Setup installer".

Tests: `Tests/Services/Setup/*`, `Tests/Routes/SetupInstallTerminalTests.cs` (rejection paths
only; a passing install test would install software), `Tests/wwwroot/js/setup-panel.test.mjs`.
