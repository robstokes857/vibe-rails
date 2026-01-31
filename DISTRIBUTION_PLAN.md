# VibeRails Distribution Strategy - Refined Plan

## Context

Comparing two approaches:
1. **Original VSCODE_EXTENSION_PLAN.md**: Simple, focused on extension only. Assumes backend is available.
2. **New exploration**: Full distribution strategy with AOT builds, installers, auto-download.

**Key insight from original plan**: The extension is already working well as-is. The real question is: how do users get the backend executable (`vb` or `vb.exe`)?

---

## Recommended Hybrid Approach

### Phase 1: Simplify Distribution First (Ship Now)

Start with the simplest possible distribution that works:

**1. Create Self-Contained AOT Build**
- Build `vb.exe` (Windows x64) and `vb` (Linux x64) as self-contained AOT binaries
- Embed wwwroot as resources (single-file distribution)
- Publish to GitHub Releases with each version

**2. Manual Installation (Short Term)**
- Users download `vb.exe` / `vb` from GitHub Releases
- Users add to PATH manually OR configure `viberails.executablePath`
- VS Code extension finds it via existing resolution logic

**3. VS Code Extension (Already Working)**
- Publish current extension to VS Code Marketplace as-is
- README includes: "Install backend from GitHub releases first"
- Extension finds backend via PATH or config setting

**Why this first:**
- Get something shipped quickly
- Validate the AOT build works correctly
- See which platform users actually need
- Get feedback before building installers

---

### Phase 2: Add Convenience Installers (Later)

Once Phase 1 is validated, add install scripts:

**Windows PowerShell Installer**
```powershell
# One-liner install
iwr https://viberails.dev/install.ps1 | iex
```

**Linux/Mac Bash Installer**
```bash
curl -fsSL https://viberails.dev/install.sh | bash
```

These scripts:
- Download from GitHub releases
- Install to `~/.viberails/bin/`
- Add to PATH
- Verify checksums

---

### Phase 3: Extension Auto-Download (Optional)

If users find manual installation painful, add auto-download to extension:

- Extension checks globalStorage first
- If not found, offers to download
- Users can opt-in or use manual PATH setup

**Skip this if**: Manual installation + PATH works well enough.

---

## Immediate Action Plan

### Step 1: Configure AOT Build

**File: `VibeRails/VibeRails.csproj`**

Add publish configuration:

```xml
<PropertyGroup>
  <!-- Enable AOT for release builds -->
  <PublishAot Condition="'$(Configuration)' == 'Release'">true</PublishAot>
  <InvariantGlobalization>false</InvariantGlobalization>
  <PublishTrimmed>true</PublishTrimmed>
  <PublishSingleFile>false</PublishSingleFile>

  <!-- Embed wwwroot as resources -->
  <EnableDefaultEmbeddedResourceItems>false</EnableDefaultEmbeddedResourceItems>
</PropertyGroup>

<ItemGroup>
  <!-- Embed all wwwroot files -->
  <EmbeddedResource Include="wwwroot\**\*" />
</ItemGroup>
```

---

### Step 2: Support Embedded Resources in Program.cs

**File: `VibeRails/Program.cs`**

Modify wwwroot resolution (around lines 11-14):

```csharp
// Get the executable's directory (where wwwroot lives)
string exeDirectory = AppContext.BaseDirectory;
string webRootPath = Path.Combine(exeDirectory, "wwwroot");

// If wwwroot doesn't exist, extract from embedded resources (AOT build)
if (!Directory.Exists(webRootPath))
{
    webRootPath = Path.Combine(Path.GetTempPath(), "viberails-wwwroot");
    ExtractEmbeddedWwwroot(webRootPath);
}
```

Add extraction method before `Main()`:

```csharp
static void ExtractEmbeddedWwwroot(string targetPath)
{
    var assembly = System.Reflection.Assembly.GetExecutingAssembly();
    var resourceNames = assembly.GetManifestResourceNames()
        .Where(name => name.Contains("wwwroot"));

    foreach (var resourceName in resourceNames)
    {
        // Convert resource name to file path
        // e.g., "VibeRails.wwwroot.app.js" → "app.js"
        var relativePath = resourceName
            .Replace("VibeRails.wwwroot.", "")
            .Replace("VibeRails.", "") // fallback
            .Replace('.', Path.DirectorySeparatorChar);

        // Restore known extensions
        if (relativePath.EndsWith(Path.DirectorySeparatorChar + "js"))
            relativePath = relativePath.Substring(0, relativePath.Length - 2) + ".js";
        if (relativePath.EndsWith(Path.DirectorySeparatorChar + "css"))
            relativePath = relativePath.Substring(0, relativePath.Length - 3) + ".css";
        if (relativePath.EndsWith(Path.DirectorySeparatorChar + "html"))
            relativePath = relativePath.Substring(0, relativePath.Length - 4) + ".html";

        var fullPath = Path.Combine(targetPath, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        using var stream = assembly.GetManifestResourceStream(resourceName);
        using var fileStream = File.Create(fullPath);
        stream!.CopyTo(fileStream);
    }
}
```

**Note**: Resource name mapping is tricky. May need adjustment based on actual embedded names. Test by calling `assembly.GetManifestResourceNames()` and logging results.

---

### Step 3: Create Build Script

**File: `Scripts/build-release.ps1`** (NEW)

```powershell
#!/usr/bin/env pwsh
# Build self-contained AOT releases for distribution

$ErrorActionPreference = "Stop"

$platforms = @(
    @{ RID = "win-x64"; Output = "vb.exe" },
    @{ RID = "linux-x64"; Output = "vb" }
)

$outputDir = "dist"
Remove-Item $outputDir -Recurse -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $outputDir | Out-Null

foreach ($platform in $platforms) {
    $rid = $platform.RID
    $output = $platform.Output

    Write-Host "`n=== Building for $rid ===" -ForegroundColor Cyan

    $platformDir = "$outputDir/$rid"

    dotnet publish VibeRails/VibeRails.csproj `
        --configuration Release `
        --runtime $rid `
        --self-contained true `
        -p:PublishAot=true `
        -o $platformDir

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed for $rid"
        exit 1
    }

    # Find the compiled executable (may have different name)
    $exe = Get-ChildItem $platformDir -Filter "VibeRails*" -File |
           Where-Object { $_.Extension -eq ".exe" -or $_.Extension -eq "" } |
           Select-Object -First 1

    if ($exe) {
        # Rename to 'vb' or 'vb.exe'
        $newName = Join-Path $platformDir $output
        Move-Item $exe.FullName $newName -Force

        # Calculate SHA256
        $hash = Get-FileHash $newName -Algorithm SHA256
        $hash.Hash.ToLower() | Out-File "$newName.sha256" -NoNewline

        $size = [math]::Round((Get-Item $newName).Length / 1MB, 2)
        Write-Host "✓ Built: $output ($size MB)" -ForegroundColor Green
        Write-Host "  SHA256: $($hash.Hash.ToLower())" -ForegroundColor DarkGray
    } else {
        Write-Error "Could not find executable in $platformDir"
        exit 1
    }
}

Write-Host "`n=== All builds complete ===" -ForegroundColor Green
Write-Host "`nRelease artifacts in: $outputDir" -ForegroundColor Cyan
```

---

### Step 4: Update VS Code Extension README

**File: `vscode-viberails/README.md`**

```markdown
# VibeRails VS Code Extension

Launch the VibeRails dashboard directly inside VS Code.

## Prerequisites

Install the VibeRails backend:

### Option 1: Download from Releases (Recommended)
1. Go to [Releases](https://github.com/yourusername/VibeControl2/releases)
2. Download `vb.exe` (Windows) or `vb` (Linux) for your platform
3. Add to system PATH or configure extension setting

### Option 2: Build from Source
```bash
git clone https://github.com/yourusername/VibeControl2
cd VibeControl2
./Scripts/build-release.ps1
# Binary will be in dist/win-x64/vb.exe or dist/linux-x64/vb
```

## Configuration

**viberails.executablePath** (optional)
- Path to `vb` executable
- If not set, extension searches system PATH and common development locations

## Usage

1. Click the "VibeRails" button in the status bar (bottom left)
2. Dashboard opens in a VS Code panel
3. Close the panel or click "Exit" to stop the backend

## Features

- Embedded dashboard with full feature set
- Runs in workspace context (local mode)
- No browser required
- Auto-stops backend when panel closes
```

---

## Testing Plan

### 1. AOT Build Test
```powershell
# Build
.\Scripts\build-release.ps1

# Test Windows build
.\dist\win-x64\vb.exe --open-browser

# Verify:
# - Dashboard opens in browser
# - All assets load (no 404s)
# - Environments page works
# - Agents page works
```

### 2. PATH Installation Test
```powershell
# Copy to user bin directory
mkdir $env:USERPROFILE\.viberails\bin -Force
copy .\dist\win-x64\vb.exe $env:USERPROFILE\.viberails\bin\

# Add to PATH (user environment)
$path = [Environment]::GetEnvironmentVariable("Path", "User")
[Environment]::SetEnvironmentVariable("Path", "$path;$env:USERPROFILE\.viberails\bin", "User")

# Restart terminal, test
vb --version  # Should work from any directory
```

### 3. VS Code Extension Test
```
1. Ensure vb.exe is in PATH
2. Open VS Code
3. Install extension (.vsix)
4. Click "VibeRails" in status bar
5. Verify dashboard opens in panel
6. Close panel
7. Check task manager - backend should exit
```

---

## File Change Summary

### New Files
- `Scripts/build-release.ps1` - AOT build script for releases

### Modified Files
- `VibeRails/VibeRails.csproj` - AOT configuration, embedded resources
- `VibeRails/Program.cs` - Extract embedded wwwroot on startup
- `vscode-viberails/README.md` - Installation instructions

### GitHub Release Assets (created by build script)
- `vb.exe` (Windows x64, self-contained)
- `vb.exe.sha256`
- `vb` (Linux x64, self-contained)
- `vb.sha256`

---

## Future Enhancements (Post-Launch)

**After validating Phase 1 works well:**

1. **Install Scripts** (`Scripts/install.ps1`, `Scripts/install.sh`)
   - One-line installers that download + configure PATH
   - Optional: Auto-update checks

2. **Extension Auto-Download** (`vscode-viberails/src/download-manager.ts`)
   - Download backend to extension globalStorage if not found
   - Only add if users find manual installation too complex

3. **Platform Expansion**
   - macOS x64 / ARM64 builds
   - Auto-detect and build for user's platform

4. **Update Mechanism**
   - CLI: `vb update` command
   - Extension: Check for updates on launch

---

## Why This Approach

**Advantages:**
- ✅ Ship quickly with minimal changes
- ✅ Single-file distribution (no wwwroot folder needed)
- ✅ Works for both CLI and VS Code users
- ✅ No complex installer required at first
- ✅ VS Code extension already functional

**Simplicity:**
- No installer scripts to maintain initially
- No auto-download complexity in extension
- Users comfortable with PATH management can start immediately
- Can iterate based on real feedback

**Path Forward:**
1. Validate AOT build works correctly
2. Ship extension to marketplace
3. Gather user feedback
4. Add installers only if manual setup is painful
5. Add auto-download only if PATH setup is a common problem

---

## User Distribution Paths

### Path 1: CLI Users
1. Download `vb.exe` from GitHub releases
2. Add to PATH manually
3. Run `vb --open-browser` from anywhere

### Path 2: VS Code Extension Users
1. Install extension from Marketplace
2. Download `vb.exe` from GitHub releases (or use existing if already installed)
3. Either add to PATH or configure `viberails.executablePath`
4. Click VibeRails button in status bar

### Path 3: Both
1. Install CLI via method 1
2. Install VS Code extension
3. Extension automatically finds `vb` in PATH
4. Use from terminal OR VS Code panel
