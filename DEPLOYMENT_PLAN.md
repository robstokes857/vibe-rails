# VibeRails — GitHub Releases Deployment Plan

## Overview

Automated releases via GitHub Actions. Push a version tag → workflow builds AOT binaries for Windows and Linux → GitHub Release is created with downloadable archives and SHA256 checksums.

---

## Release Flow

### One-Time Setup

```bash
# 1. Create the GitHub repository and push
gh repo create <repo-name> --public --source=. --push

# 2. Verify .github/workflows/release.yml exists (see below)

# 3. Push master with the workflow file
git push -u origin master
```

### Triggering a Release

```bash
# 1. Ensure all changes are committed and pushed
git push

# 2. Create and push an annotated tag
git tag -a v1.0.0 -m "Release v1.0.0"
git push origin v1.0.0

# 3. Monitor the workflow
gh run watch
```

The release will appear at `https://github.com/<owner>/<repo>/releases/tag/v1.0.0`.

### Tag Naming Conventions

| Tag | Type |
|-----|------|
| `v1.0.0` | Stable release |
| `v1.1.0`, `v2.0.0` | Feature / major release |
| `v1.0.0-beta.1` | Pre-release (auto-detected by `-` in tag) |
| `v1.0.0-rc.1` | Release candidate (also pre-release) |

---

## Workflow: `.github/workflows/release.yml`

### Trigger

```yaml
on:
  push:
    tags:
      - "v*.*.*"
```

### Build Job (Matrix Strategy)

Two parallel builds on native runners:

| Runner | RID | Output |
|--------|-----|--------|
| `windows-latest` | `win-x64` | `vb-win-x64.zip` |
| `ubuntu-latest` | `linux-x64` | `vb-linux-x64.tar.gz` |

Each build:
1. Checkout with `submodules: recursive` (PtyNet has nested submodules)
2. Setup .NET 10.0 preview via `actions/setup-dotnet@v4`
3. Linux only: `apt-get install clang zlib1g-dev` (AOT native dependencies)
4. `dotnet publish` with AOT flags (matching `build/build.ps1`)
5. Package as zip (Windows) or tar.gz (Linux)
6. Compute SHA256 checksum
7. Upload via `actions/upload-artifact@v4`

Archives are used instead of bare binaries because `wwwroot/` must ship alongside the executable.

### Release Job

Runs after both builds complete:
1. Downloads all artifacts
2. Assembles checksum summary
3. Creates GitHub Release via `softprops/action-gh-release@v2`
4. Auto-generates release notes from commits since last tag
5. Tags with `-` (e.g., `-beta.1`) are auto-marked as pre-release

### Release Assets

Each release includes 4 files:
- `vb-win-x64.zip` — Windows x64 AOT binary + wwwroot
- `vb-win-x64.zip.sha256` — SHA256 checksum
- `vb-linux-x64.tar.gz` — Linux x64 AOT binary + wwwroot
- `vb-linux-x64.tar.gz.sha256` — SHA256 checksum

---

## Full Workflow YAML

```yaml
name: Release

on:
  push:
    tags:
      - "v*.*.*"

permissions:
  contents: write

env:
  DOTNET_VERSION: "10.0.x"
  DOTNET_QUALITY: "preview"
  PROJECT_PATH: "VibeRails/VibeRails.csproj"
  CONFIGURATION: "Release"
  FRAMEWORK: "net10.0"

jobs:
  build:
    strategy:
      matrix:
        include:
          - os: windows-latest
            rid: win-x64
            artifact-name: vb-win-x64
            archive-ext: zip
          - os: ubuntu-latest
            rid: linux-x64
            artifact-name: vb-linux-x64
            archive-ext: tar.gz

    runs-on: ${{ matrix.os }}
    name: Build (${{ matrix.rid }})

    steps:
      - name: Checkout with submodules
        uses: actions/checkout@v4
        with:
          submodules: recursive

      - name: Setup .NET
        uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}
          dotnet-quality: ${{ env.DOTNET_QUALITY }}

      - name: Install AOT dependencies (Linux)
        if: runner.os == 'Linux'
        run: |
          sudo apt-get update
          sudo apt-get install -y --no-install-recommends clang zlib1g-dev

      - name: Publish AOT
        run: >
          dotnet publish ${{ env.PROJECT_PATH }}
          -c ${{ env.CONFIGURATION }}
          -f ${{ env.FRAMEWORK }}
          -r ${{ matrix.rid }}
          --self-contained true
          -o publish/${{ matrix.rid }}
          /p:PublishAot=true
          /p:StripSymbols=true
          /p:InvariantGlobalization=true

      - name: Create archive (Windows)
        if: runner.os == 'Windows'
        shell: pwsh
        run: |
          Compress-Archive -Path "publish/${{ matrix.rid }}/*" -DestinationPath "${{ matrix.artifact-name }}.zip"

      - name: Create archive (Linux)
        if: runner.os == 'Linux'
        run: |
          tar -czf "${{ matrix.artifact-name }}.tar.gz" -C "publish/${{ matrix.rid }}" .

      - name: Compute checksum (Windows)
        if: runner.os == 'Windows'
        shell: pwsh
        run: |
          $hash = (Get-FileHash -Algorithm SHA256 "${{ matrix.artifact-name }}.zip").Hash.ToLowerInvariant()
          "$hash  ${{ matrix.artifact-name }}.zip" | Out-File -Encoding ascii "${{ matrix.artifact-name }}.zip.sha256"

      - name: Compute checksum (Linux)
        if: runner.os == 'Linux'
        run: |
          sha256sum "${{ matrix.artifact-name }}.tar.gz" > "${{ matrix.artifact-name }}.tar.gz.sha256"

      - name: Upload build artifact
        uses: actions/upload-artifact@v4
        with:
          name: ${{ matrix.artifact-name }}
          path: |
            ${{ matrix.artifact-name }}.${{ matrix.archive-ext }}
            ${{ matrix.artifact-name }}.${{ matrix.archive-ext }}.sha256
          retention-days: 1

  release:
    needs: build
    runs-on: ubuntu-latest
    name: Create Release

    steps:
      - name: Download all artifacts
        uses: actions/download-artifact@v4
        with:
          path: artifacts
          merge-multiple: true

      - name: Build checksums body
        run: |
          echo "## SHA256 Checksums" > checksums.md
          echo "" >> checksums.md
          echo '```' >> checksums.md
          cat artifacts/*.sha256 >> checksums.md
          echo '```' >> checksums.md

      - name: Extract version from tag
        id: version
        run: echo "version=${GITHUB_REF_NAME#v}" >> "$GITHUB_OUTPUT"

      - name: Create GitHub Release
        uses: softprops/action-gh-release@v2
        with:
          tag_name: ${{ github.ref_name }}
          name: VibeRails ${{ steps.version.outputs.version }}
          draft: false
          prerelease: ${{ contains(github.ref_name, '-') }}
          generate_release_notes: true
          body_path: checksums.md
          files: |
            artifacts/vb-win-x64.zip
            artifacts/vb-win-x64.zip.sha256
            artifacts/vb-linux-x64.tar.gz
            artifacts/vb-linux-x64.tar.gz.sha256
```

---

## Optional: CI Workflow (`.github/workflows/ci.yml`)

Build verification on every push/PR to master:

```yaml
name: CI

on:
  push:
    branches: [master]
  pull_request:
    branches: [master]

env:
  DOTNET_VERSION: "10.0.x"
  DOTNET_QUALITY: "preview"

jobs:
  build:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with:
          submodules: recursive

      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: ${{ env.DOTNET_VERSION }}
          dotnet-quality: ${{ env.DOTNET_QUALITY }}

      - name: Install AOT dependencies
        run: |
          sudo apt-get update
          sudo apt-get install -y --no-install-recommends clang zlib1g-dev

      - name: Build
        run: dotnet build VibeRails/VibeRails.csproj -c Release

      # Uncomment after fixing Tests/Tests.csproj project reference
      # - name: Test
      #   run: dotnet test Tests/Tests.csproj --verbosity normal
```

---

## Notes

- **Archives required**: The publish output includes `wwwroot/` (HTML, JS, CSS, images) alongside the binary. Users must extract the archive and run from the extracted directory.
- **.NET 10.0 preview**: The workflow uses `dotnet-quality: preview`. Remove that line once .NET 10 reaches GA.
- **Submodules**: PtyNet has nested submodules (`dep/winpty`, `dep/terminal`), so `submodules: recursive` is mandatory.
- **Tests**: `Tests/Tests.csproj` currently references the deleted `VibeControl.csproj`. Update to `VibeRails.csproj` before enabling tests in CI.

## Verification

1. Push workflow files to master
2. Create a test tag: `git tag -a v0.0.1-test -m "Test release" && git push origin v0.0.1-test`
3. Check the Actions tab for the workflow run
4. Verify the release has 4 assets (2 archives + 2 checksums)
5. Download and extract each archive — confirm `vb`/`vb.exe` and `wwwroot/` are present
6. Clean up: `gh release delete v0.0.1-test -y && git push --delete origin v0.0.1-test && git tag -d v0.0.1-test`
