# VibeRails Installation

One-line installers for VibeRails (vb) - download and install the latest release.

## Windows

Open PowerShell and run:

```powershell
irm https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.ps1 | iex
```

This will:
- Download the latest Windows release (`vb-win-x64.zip`)
- Verify SHA256 checksum
- Extract to `~/.vibe_rails`
- Install BertV2 model assets (used for semantic history search)
- Add to your PATH

## Linux/macOS

Open terminal and run:

```bash
# Using wget
wget -qO- https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.sh | bash

# Or using curl
curl -fsSL https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.sh | bash
```

This will:
- Detect your platform/architecture and download the correct release asset:
  - `vb-linux-x64.tar.gz`
  - `vb-osx-arm64.tar.gz`
- Verify SHA256 checksum
- Extract to `~/.vibe_rails`
- Install BertV2 model assets (used for semantic history search)
- Update your shell configuration (`.bashrc`, `.zshrc`, `.zprofile`, or `.profile`)

## After Installation

Restart your terminal or run:

**Windows (PowerShell):**
```powershell
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","User")
```

**Linux/macOS:**
```bash
source ~/.bashrc  # or ~/.zshrc / ~/.zprofile depending on your shell
```

Then verify installation:
```bash
vb --version
```

## Updates and Rollback

Re-run the same one-line installer to update. The installer downloads and validates
the complete release in a private staging directory before changing
`~/.vibe_rails`, then overlays application files without deleting the database or
any other user data. Close running VibeRails windows first: the installer refuses
to replace `vb.exe` while it is still in use.

The installer does not perform an automatic binary or database rollback. To
return to an older release, first back up `~/.vibe_rails/state.db`, then use the
older platform archive and checksum from GitHub Releases and repeat the same
overlay. A binary rollback does not reverse database migrations; check that
release's notes before reverting.

## Usage

Start the VibeRails dashboard:
```bash
vb
```

For more information, visit: https://github.com/robstokes857/vibe-rails

---

*Last checked: 2026-08-06T18:21:17Z by opencode (glm-5.2)*
