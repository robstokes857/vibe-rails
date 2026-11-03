# Run: pwsh -NoProfile -File Tests/Deployment/ReleasePreflightTests.ps1
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$parseErrors = $null
$tokens = $null
$syntax = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repoRoot 'deploy/deploy.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ($parseErrors -join [Environment]::NewLine) }

# Load only read-only preflight helpers, never the release script's main body.
foreach ($name in @('Assert-NativeCommandSucceeded', 'Test-PreFlightChecks',
    'Get-LatestReleaseVersion', 'Test-GitIndexLockAbandoned')) {
    $definition = $syntax.Find({ param($node)
        $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $false)
    if ($null -eq $definition) { throw "Missing release helper: $name" }
    . ([scriptblock]::Create($definition.Extent.Text))
}

$AppSettingsFile = Join-Path $repoRoot 'VibeRails/appsettings.json'
$ProjectFile = Join-Path $repoRoot 'VibeRails/VibeRails.csproj'
$PackageJsonFile = Join-Path $repoRoot 'vscode-viberails/package.json'
$GithubRepo = 'fixture/repository'
$script:gitFailure = ''
$script:gitCalls = @()
$script:remoteCommit = 'local-commit'
$script:ghExitCode = 0
$script:ghJson = '[{"tagName":"v10.13.7"}]'
$script:ghArguments = @()

# These replace every external command used by the loaded helpers. No network, real git
# index, release, or installed application is touched.
function git {
    $command = $args -join ' '
    $script:gitCalls += $command
    $global:LASTEXITCODE = if ($command -eq $script:gitFailure) { 128 } else { 0 }
    if ($LASTEXITCODE -ne 0) { return }
    switch ($command) {
        'rev-parse --git-dir' { '.git' }
        'branch --show-current' { 'main' }
        'rev-parse HEAD' { 'local-commit' }
        'rev-parse --verify refs/remotes/origin/main' { $script:remoteCommit }
        'rev-list --count HEAD..origin/main' { '0' }
    }
}
function gh {
    $script:ghArguments = $args
    $global:LASTEXITCODE = $script:ghExitCode
    $script:ghJson
}
function Assert-Fails([scriptblock]$Action, [string]$Message) {
    $failure = $null
    try { & $Action } catch { $failure = $_ }
    if ($null -eq $failure -or -not $failure.ToString().Contains($Message)) {
        throw "Expected failure containing '$Message'; received '$failure'."
    }
}

Test-PreFlightChecks
foreach ($case in @(
    @('branch --show-current', 'Resolving the release branch'),
    @('status --porcelain', 'Checking the working tree'),
    @('fetch origin main', 'Fetching the release branch'),
    @('rev-parse HEAD', 'Resolving the local release commit'),
    @('rev-parse --verify refs/remotes/origin/main', 'Resolving the remote release commit'),
    @('rev-list --count HEAD..origin/main', 'Comparing the release branch with origin')
)) {
    $script:gitFailure = $case[0]
    $script:remoteCommit = 'different-commit'
    Assert-Fails { Test-PreFlightChecks } $case[1]
}
$script:gitFailure = ''

if ((Get-LatestReleaseVersion).ToString() -ne '10.13.7') { throw 'Release version was parsed incorrectly.' }
if (($script:ghArguments -join ' ') -notmatch '--json tagName') { throw 'Release lookup must use structured output.' }
$script:ghJson = '[]'
if ((Get-LatestReleaseVersion).ToString() -ne '0.0.0') { throw 'A repository without releases must be supported.' }
$script:ghExitCode = 1
Assert-Fails { Get-LatestReleaseVersion } 'Reading the latest GitHub release'

if ($IsWindows) {
    $lockPath = Join-Path ([IO.Path]::GetTempPath()) ('vb-preflight-' + [Guid]::NewGuid().ToString('N') + '.lock')
    try {
        [IO.File]::WriteAllText($lockPath, '')
        (Get-Item -LiteralPath $lockPath).LastWriteTime = (Get-Date).AddMinutes(-5)
        $script:inventoryFails = $true
        $script:inventory = @()
        function Get-CimInstance {
            if ($script:inventoryFails) { throw 'Process inventory unavailable' }
            $script:inventory
        }
        if (Test-GitIndexLockAbandoned -LockPath $lockPath) {
            throw 'An unavailable process inventory must never authorize deleting a git lock.'
        }
        $script:inventoryFails = $false
        $script:inventory = @([pscustomobject]@{ CommandLine = 'git commit' })
        if (Test-GitIndexLockAbandoned -LockPath $lockPath) { throw 'An active git process must preserve the lock.' }
        $script:inventory = @()
        if (-not (Test-GitIndexLockAbandoned -LockPath $lockPath)) { throw 'An unused old lock should be recognized.' }
    } finally {
        Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue
    }
}

Write-Host 'Passed: release preflight fails closed on git, GitHub and process inventory errors. No release was run.'
