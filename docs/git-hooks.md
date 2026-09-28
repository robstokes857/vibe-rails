# Git hook installation

Moved verbatim from the root `AGENTS.md` on 2026-09-27; last reviewed 2026-08-06 (v1.9.11).
Check `VibeRails/Services/HookInstallationService.cs` and its tests before relying on the
details below. The rule contract that the hooks enforce is in
[Services/VCA/AGENTS.md](../VibeRails/Services/VCA/AGENTS.md).

## Git Hook Installation System

### Overview

VibeRails includes a sophisticated git hook installation system that automatically enforces VCA (Vibe Control Architecture) rules at commit time. The system has been completely refactored from hardcoded scripts to a modular, testable, and maintainable architecture.

### Architecture

#### HookInstallationService ([Services/HookInstallationService.cs](../VibeRails/Services/HookInstallationService.cs))

**Purpose**: Manage installation and uninstallation of git hooks for VCA enforcement

**Key Improvements (Refactored 2026-02-06)**:
- ✅ **Extracted scripts to files** - Hook scripts moved from C# strings to [scripts/](../VibeRails/scripts/) directory
- ✅ **Proper error handling** - Returns detailed `HookInstallationResult` with specific error types
- ✅ **Structured logging** - Integrated with `ILogger<T>` for comprehensive diagnostics
- ✅ **Atomic operations** - Rollback support if installation partially fails
- ✅ **Configuration support** - Respects `app_config.json` settings for auto-install behavior
- ✅ **Cross-platform safe** - Handles Windows, Linux, and macOS correctly
- ✅ **Comprehensive tests** - Full test coverage in [Tests/Services/HookInstallationServiceTests.cs](../Tests/Services/HookInstallationServiceTests.cs)

**Hook Scripts**:
1. **pre-commit-hook.sh** - Validates VCA rules before commit
   - Runs the standalone `vb --vca-hook pre-commit` host against the Git index snapshot (not unstaged working-tree edits)
   - Shows a dedicated console when a Windows Git GUI captures hook output
   - Blocks STOP violations and validation errors; COMMIT violations continue to commit-msg
   - Allows bypass with `git commit --no-verify`

2. **commit-msg-hook.sh** - Validates COMMIT-level acknowledgments
   - Runs `vb --vca-hook commit-msg` through the same validation engine
   - Ensures required acknowledgment tokens include a non-empty reason
   - Enforces COMMIT-level rule compliance

**Installation Behavior**:
- **Auto-install on startup** - Hooks installed automatically when VibeRails starts (configurable)
- **Preserves existing hooks** - Inserts VCA ahead of existing shell-hook exits and chains non-shell/binary/symlink hooks through a preserved sidecar
- **App-versioned health checks** - Installed hooks carry the running VibeRails version (for example `1.9.8`); startup detects missing, disabled, stale/older-version, partial, missing-launcher, or mismatched-launcher hooks and replaces them
- **Git-aware path resolution** - Honors linked worktrees and `core.hooksPath`
- **Safe hook chaining** - Runs before existing shell hooks and preserves non-shell hooks as executable sidecars
- **Safe uninstallation** - Removes only VibeRails sections, keeps other hooks intact

**Key Methods**:

```csharp
// Install both pre-commit and commit-msg hooks
Task<HookInstallationResult> InstallHooksAsync(string repoPath, CancellationToken ct);

// Uninstall both hooks
Task<HookInstallationResult> UninstallHooksAsync(string repoPath, CancellationToken ct);

// Install individual hooks
Task<HookInstallationResult> InstallPreCommitHookAsync(string repoPath, CancellationToken ct);
Task<HookInstallationResult> UninstallPreCommitHookAsync(string repoPath, CancellationToken ct);

// Resolve Git's effective hook path and inspect both hooks
Task<GitHooksStatus> GetStatusAsync(string repoPath, CancellationToken ct);
```

**Error Handling**:

The service returns detailed error information via `HookInstallationResult`:

```csharp
public enum HookInstallationError
{
    HooksDirectoryNotFound,
    HooksDirectoryCreationFailed,
    PermissionDenied,
    FileReadError,
    FileWriteError,
    ChmodExecutionFailed,
    ScriptResourceNotFound,
    PartialInstallationFailure,
    UnknownError
}
```

**Configuration** ([appsettings.json](../VibeRails/appsettings.json)):

```json
{
  "VibeRails": {
    "Hooks": {
      "AutoInstall": true,
      "InstallOnStartup": true
    }
  }
}
```

**Usage Examples**:

```csharp
// Install hooks
var result = await hookService.InstallHooksAsync(repoPath, cancellationToken);
if (!result.Success)
{
    Console.Error.WriteLine($"Installation failed: {result.ErrorMessage}");
    if (result.Details != null)
    {
        Console.Error.WriteLine($"Details: {result.Details}");
    }
}

// Check if installed
if ((await hookService.GetStatusAsync(repoPath, cancellationToken)).IsInstalled)
{
    Console.WriteLine("Hooks are installed");
}

// Uninstall hooks
var uninstallResult = await hookService.UninstallHooksAsync(repoPath, cancellationToken);
```

**API Endpoints**:

```
GET  /api/v1/hooks/status        # Check if hooks are installed
POST /api/v1/hooks/install       # Install hooks via API
DELETE /api/v1/hooks             # Uninstall hooks via API
POST /api/v1/hooks/preview       # Run the real pre-commit pipeline and capture its console
```

**Testing**:

Comprehensive test suite covers:
- ✅ Fresh installation in empty repository
- ✅ Creating hooks directory if it doesn't exist
- ✅ Appending to existing hooks from other tools
- ✅ Replacing old VibeRails hook versions
- ✅ Uninstalling while preserving other hooks
- ✅ Permission error handling
- ✅ Logging verification
- ✅ Atomic rollback on partial failures

Run tests:
```bash
cd Tests
dotnet test --filter "HookInstallationServiceTests"
```

**Design Patterns**:
- **Dependency Injection** - `ILogger<T>` injected for structured logging
- **Result Pattern** - Methods return `HookInstallationResult` instead of bool
- **Template Method** - Common installation logic extracted to `InstallHookAsync()`
- **Atomic Operations** - Rollback on failure ensures consistent state

**File Locations**:
```
VibeRails/
├── scripts/                          # Hook script templates
│   ├── pre-commit-hook.sh           # Pre-commit validation script
│   ├── commit-msg-hook.sh           # Commit message validation script
│   └── post-commit-hook.sh          # Post-commit (job trigger) script
├── Services/
│   ├── HookInstallationService.cs   # Main service implementation
│   └── HookInstallationResult.cs    # Result types
└──appsettings.json                   # Application configuration

.git/hooks/                           # Git hooks directory (per repo)
├── pre-commit                        # Installed pre-commit hook
└── commit-msg                        # Installed commit-msg hook
```

**Logging Output**:

The service provides comprehensive logging:
- Information: Hook installation start/completion
- Debug: Script loading, file operations, hook content details
- Warning: Missing end markers, partial content
- Error: Permission issues, file I/O failures, chmod failures

**Cross-Platform Behavior**:
- **Windows**: Hooks work via Git Bash (no chmod needed)
- **Linux/macOS**: Hooks made executable via `chmod +x`
- **All platforms**: Scripts use `#!/bin/sh` shebang for POSIX compatibility

**Security Considerations**:
- Scripts loaded from application directory, not user input
- File paths validated to prevent directory traversal
- Markers prevent accidental corruption of other hooks
- No shell injection vulnerabilities in hook execution

**Migration Notes**:

Previous implementation had these issues (fixed):
❌ Scripts hardcoded as C# strings (hard to maintain)
❌ Returns bool only (no error context)
❌ No logging (silent failures)
❌ No configuration support
❌ chmod failures ignored
❌ No tests
❌ No rollback on partial failure

Current implementation:
✅ Scripts in separate files (easy to edit and test)
✅ Detailed error results with error types
✅ Structured logging throughout
✅ Configurable auto-install behavior
✅ chmod failures reported
✅ Comprehensive test coverage
✅ Atomic operations with rollback
