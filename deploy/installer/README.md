# Windows installer

The tag release workflow publishes `VibeRails-Setup-<version>-win-x64.exe` and
its SHA-256 file alongside `vb-win-x64.zip`. Setup uses Inno Setup 6.7.3, installs
for the current user without elevation, and adds a branded Start menu shortcut.
The desktop shortcut and opening the dashboard at the end are optional. Both
shortcuts run `vb.exe --web` with the user's profile as the working directory;
the existing console window is minimized.

## Build

Install Inno Setup 6.7.3 and the Visual Studio C++ build tools with a Windows SDK.
Use PowerShell 7 to publish a clean NativeAOT payload and package it:

```powershell
dotnet publish VibeRails/VibeRails.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64 /p:PublishAot=true /p:StripSymbols=true /p:InvariantGlobalization=true
./deploy/prepare-windows-payload.ps1 -PayloadDirectory artifacts/win-x64
./deploy/build-installer.ps1 -PayloadDirectory artifacts/win-x64 -Version 10.11.29
```

`prepare-windows-payload.ps1` copies app-local Microsoft VC runtime DLLs from
Visual Studio's x64 redistributable directory. These ship in the ZIP and VSIX
as well. It never installs the machine-wide VC redistributable. A custom licensed
redistributable location can be supplied with `-RuntimeDirectory`.
Discovery prefers the newest numeric MSVC directory and also supports toolset
aliases such as `v143` and `v145` without treating them as version numbers.
The isolated runtime-discovery and payload-copy regression runs with
`pwsh -NoProfile -File Tests/Deployment/WindowsRuntimePayloadTests.ps1` and
as part of `dotnet test Tests/Tests.csproj`.

The builder requires known assets and rejects unrecognized payload files. It
generates explicit Inno file entries from the publish output; symbols, import
libraries and assembly XML documentation are excluded. It extracts the bundled
BERT model at build time and ships the extracted model instead of duplicate ZIP
and gzip archives, so installation does not need a download, .NET or
PowerShell. Inno itself is a build dependency, not a target-machine prerequisite.
Git, PowerShell 7 and an AI CLI remain prerequisites managed through the existing
Setup screen.

## Data, updates and uninstall

The installation directory stays `%USERPROFILE%\.vibe_rails` for CLI, MCP and
git-hook compatibility. Setup refuses `/DIR` overrides. A stable AppId enables
in-place upgrades and a per-user Windows Installed Apps entry. There is no
service, login startup registration, database migration or OS scheduled task.

Only exact application files are owned by the uninstaller. Configuration files
and bundled/runtime models use `onlyifdoesntexist uninsneveruninstall`. Databases,
SQLite sidecars, credentials, environments, user scripts, logs and sandboxes
are never installer payload entries. There is no wildcard uninstall deletion.
Uninstall preserves these files and the installation directory when nonempty.
Retired application files from past installer versions remain in Inno's uninstall
log until uninstall; upgrades do not recursively clean the mixed data directory.

Setup and uninstall serialize through a per-profile mutex across login sessions.
Before changes, they check directory ownership, payload destination junctions and
symlinks, and exclusive writable access to files they replace/remove.
New installations explicitly give the installation folder to the current user,
including when Setup was launched from an elevated shell. Existing folders must
already belong to that user; Setup never takes ownership of an existing folder.
Busy setup offers retry/cancel; silent setup and uninstall fail. They do not terminate
processes or schedule replacement on reboot. This is a preflight check, not an
atomic directory swap: a process starting afterward can still cause a normal
Inno file-copy error. Close sessions using this installed executable before an
upgrade; applications using other executable locations may continue running.

PATH comparisons use complete, normalized segments. An existing user entry is
preserved; only an entry added by Setup is recorded under
`HKCU\Software\VibeRails\Installer` and removed on uninstall. Other segments keep
their original spelling and order. Open a new terminal to use the updated PATH.

## Signing and verification

Release builds use `-ReleaseSigning`. Inno's `trusted` SignTool invokes
`sign.ps1` for both the embedded uninstaller and the outer Setup EXE through the
same TrustedSigning 0.4.1 module and identity as the existing binary signing
action. Azure credentials are supplied only in that step's environment. Invalid
signatures fail the build; the checksum is written after signing. Local builds
are unsigned and must not be represented as signed releases.
The compiler's signing command uses Inno's `$q`/`$$` escaping so paths remain
intact. The Windows PR check compiles an inert signer probe to test this command
without Azure credentials or a real signature.

Run the lifecycle regression on Windows:

```powershell
./deploy/installer/test.ps1 -PayloadDirectory artifacts/win-x64 -Compiler 'C:/Program Files (x86)/Inno Setup 6/ISCC.exe'
```

It compiles a fixture installer with a separate AppId, PATH ownership key and
shortcut names. Its installation root is under `.test-artifacts`, including
spaces, non-ASCII text, straight and curly apostrophes. It temporarily modifies
user PATH and adds fixture shortcuts, then restores them. It tests fresh model
extraction, shortcuts, repeat installs, busy-file refusal, concurrent-installer
refusal, destination-junction refusal, data preservation, uninstall/reinstall,
and pre-existing PATH ownership. It never starts the application or points a
debug build at another state database. Logs and preserved sentinel files remain
for inspection; use a new `-OutputDirectory` for a subsequent run.

Installer pull requests also run this lifecycle on Windows with an inert payload,
including elevated-runner ownership and refusal of differently owned folders.
Release CI runs these checks against its fresh NativeAOT publish. Failed setup
logs are printed and retained as workflow artifacts. Before public
distribution, also exercise the signed Setup and uninstaller as a standard user
on a clean Windows machine without .NET/VC runtimes or developer tools: check no
UAC, launch both shortcuts, complete Setup, use terminals and BERT search, and
verify publisher signatures. Hosted runners have developer runtimes installed,
so lifecycle tests alone cannot establish this clean-machine coverage.
