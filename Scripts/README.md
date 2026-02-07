# VibeRails Installation

One-line installers for VibeRails (vb) - download and install the latest release.

## Windows

Open PowerShell and run:

```powershell
irm https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.ps1 | iex
```

This will:
- Download the latest Windows release
- Verify SHA256 checksum
- Extract to `~/.vibe_rails`
- Add to your PATH

## Linux/macOS

Open terminal and run:

```bash
wget -qO- https://raw.githubusercontent.com/robstokes857/vibe-rails/main/Scripts/install.sh | bash
```

This will:
- Download the latest Linux release
- Verify SHA256 checksum
- Extract to `~/.vibe_rails`
- Update your shell configuration (`.bashrc`, `.zshrc`, or `.profile`)

## After Installation

Restart your terminal or run:

**Windows (PowerShell):**
```powershell
$env:Path = [System.Environment]::GetEnvironmentVariable("Path","User")
```

**Linux/macOS:**
```bash
source ~/.bashrc  # or ~/.zshrc depending on your shell
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
