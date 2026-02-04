# VibeRails Deployment Scripts

This directory contains all scripts related to building and deploying VibeRails.

## Scripts

### deploy.ps1
**Main deployment script** - Orchestrates the complete release process.

**Usage:**
```powershell
.\deploy.ps1
```

**What it does:**
1. **Pre-flight checks** - Ensures you're on `main` branch with clean git status
2. Prompts for new version number (e.g., 1.1.0)
3. Updates version in `app_config.json` and `package.json`
4. Builds Windows and Linux binaries via `build.ps1`
5. Packages VS Code extensions
6. Creates release archives with SHA256 checksums
7. Commits and pushes version changes
8. Creates and pushes git tag
9. Creates GitHub release with all assets

**Options:**
- `--SkipBuild` - Skip the build step (use existing artifacts)
- `--DryRun` - Preview what would happen without making changes

**Rollback:**
If anything fails, the script automatically rolls back changes.

---

### build.ps1
**Cross-platform build script** - Builds native AOT binaries for Windows and Linux.

**Usage:**
```powershell
.\build.ps1 -Project ..\VibeRails\VibeRails.csproj
```

**What it does:**
1. Builds Windows x64 binary locally
2. Builds Linux x64 binary via Docker
3. Generates SHA256 checksums
4. Outputs to `artifacts/aot/{win-x64,linux-x64}/`

**Requirements:**
- .NET 10.0 SDK
- Docker (for Linux builds)

---

### install.ps1
**Windows installer script** - One-liner installer for end users.

**Usage:**
```powershell
irm https://raw.githubusercontent.com/robstokes857/vibe-rails/main/deploy/install.ps1 | iex
```

**What it does:**
1. Downloads latest release from GitHub
2. Verifies SHA256 checksums
3. Extracts to `~/.vibe_rails`
4. Adds to user PATH

---

### install.sh
**Linux installer script** - One-liner installer for end users.

**Usage:**
```bash
curl -fsSL https://raw.githubusercontent.com/robstokes857/vibe-rails/main/deploy/install.sh | bash
```

**What it does:**
1. Downloads latest release from GitHub
2. Verifies SHA256 checksums
3. Extracts to `~/.vibe_rails`
4. Configures shell RC files

---

## Pre-Flight Checks

Before running `deploy.ps1`, the script verifies:

✓ Running in a git repository
✓ On `main` or `master` branch
✓ Working directory is clean (no uncommitted changes)
✓ Local branch is synced with remote

This ensures you don't accidentally deploy from the wrong branch or with uncommitted changes.

---

## Directory Structure

```
deploy/
├── deploy.ps1       # Main deployment orchestrator
├── build.ps1        # Cross-platform build script
├── install.ps1      # Windows installer
├── install.sh       # Linux installer
├── README.md        # This file
└── artifacts/       # Build outputs (created during deployment)
    └── aot/
        ├── win-x64/
        ├── linux-x64/
        └── release/
```

---

## Release Workflow

1. **Ensure clean state:**
   ```powershell
   git status  # Should show "nothing to commit, working tree clean"
   git checkout main
   git pull
   ```

2. **Run deployment:**
   ```powershell
   .\deploy\deploy.ps1
   ```

3. **Enter version:**
   - Type the new version (e.g., `1.1.0`)
   - Confirm with `Y`

4. **Wait for completion:**
   - Script builds, packages, and releases automatically
   - GitHub release will be created with all assets

5. **Verify release:**
   - Check https://github.com/robstokes857/vibe-rails/releases
   - Verify all assets are present (zip, tar.gz, vsix, checksums)

---

## Troubleshooting

### "Must be on 'main' or 'master' branch"
You're on a feature branch. Switch to main:
```powershell
git checkout main
```

### "Working directory must be clean"
You have uncommitted changes. Commit or stash them:
```powershell
git status
git add .
git commit -m "Your message"
# or
git stash
```

### "Local branch is behind remote"
Pull the latest changes:
```powershell
git pull
```

### "Docker is required for Linux builds"
Start Docker Desktop and ensure it's running.

### Build fails
Check that .NET 10.0 SDK is installed:
```powershell
dotnet --version
```
