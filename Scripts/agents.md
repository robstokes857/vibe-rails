# Scripts - Agent Notes

## Install scripts

`install.ps1` and `install.sh` are fetched directly from this directory via raw
GitHub URLs at install time (see `README.md`). They are **not** bundled into the
app build output — `deploy/prepare-binaries.ps1` copies the lowercase
`VibeRails/scripts/` directory (git hook scripts), not this `Scripts/` directory.

`release.yml` also publishes both scripts as release assets, stamping the release's tag
into the single empty `$ReleaseTag = ""` / `RELEASE_TAG=""` line. Keep that line as it is;
the stamp step fails the release if it cannot find it. The stamped copy installs exactly
its own release, and the copy on `main` installs the latest.
`Assert-PublisherSignature` and `Assert-PayloadMatchesCatalog` in `install.ps1` are
self-contained on purpose: release.yml extracts them by name and runs them on the signed
catalog and the packaged archive. Keep their names, or update the list in release.yml
(`ReleaseSupplyChainTests` checks the two agree). `$LastReleaseIdWithoutCatalog` is a
one-time cutoff for releases that predate `vb-win-x64.cat`; never raise it.

After modifying an install script here, there are no in-repo copies to sync. The
viberails.ai repository (VibeRails-Front) keeps its own copies under
`wwwroot/assets/install-scripts/`. Note that both scripts also install the bundled BertV2 model assets
(`Models/BertV2/`) into `~/.vibe_rails/models/bertv2/` — keep that step in sync
across `install.ps1` and `install.sh` when changing model handling.

## Other scripts

- `deploy.ps1` — thin wrapper that forwards to `deploy/deploy.ps1`.
- `download-bert-model.ps1` / `download-bert-model.sh` — manual fallback helpers to
  fetch the BertV2 ONNX model + vocab (release packages already bundle them). They pin the
  commit that last changed `VibeRails/Models/BertV2` and verify SHA-256 hashes of the git
  blobs. Update the ref and the hashes together when the model changes;
  `ReleaseSupplyChainTests` compares the hashes with the checked-in files.
- `test-vscode-extension-smoke.ps1` — smoke test for the VS Code extension.

---

*Last checked: 2026-08-06T18:21:17Z by opencode (glm-5.2)*
