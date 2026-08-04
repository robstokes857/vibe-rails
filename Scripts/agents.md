# Scripts - Agent Notes

## Install scripts

`install.ps1` and `install.sh` are fetched directly from this directory via raw
GitHub URLs at install time (see `README.md`). They are **not** bundled into the
app build output — `deploy/prepare-binaries.ps1` copies the lowercase
`VibeRails/scripts/` directory (git hook scripts), not this `Scripts/` directory.

After modifying an install script here, there are no in-repo copies to sync.
Note that both scripts also install the bundled BertV2 model assets
(`Models/BertV2/`) into `~/.vibe_rails/models/bertv2/` — keep that step in sync
across `install.ps1` and `install.sh` when changing model handling.

## Other scripts

- `deploy.ps1` — thin wrapper that forwards to `deploy/deploy.ps1`.
- `download-bert-model.ps1` / `download-bert-model.sh` — dev-only helpers to
  fetch the BertV2 ONNX model + vocab for local development.
- `test-vscode-extension-smoke.ps1` — smoke test for the VS Code extension.

---

*Last checked: 2026-08-04T19:38:40Z by opencode (glm-5.2)*
