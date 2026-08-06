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
  - `vb-osx-x64.tar.gz`
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

## Usage

Start the VibeRails dashboard:
```bash
vb
```

For more information, visit: https://github.com/robstokes857/vibe-rails

---

*Last checked: 2026-08-06T18:21:17Z by opencode (glm-5.2)*
