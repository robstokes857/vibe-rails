# Run: pwsh -NoProfile -File Tests/Deployment/WindowsRuntimePayloadTests.ps1
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$payloadScript = Join-Path $repoRoot 'deploy/prepare-windows-payload.ps1'
$parseErrors = $null
$tokens = $null
$syntax = [System.Management.Automation.Language.Parser]::ParseFile($payloadScript, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) { throw ($parseErrors -join [Environment]::NewLine) }
$discovery = $syntax.Find({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Find-VcRuntimeDirectory'
}, $false)
if ($null -eq $discovery) { throw 'VC runtime discovery was not found.' }
. ([scriptblock]::Create($discovery.Extent.Text))

$fixture = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) ('viberails-vc-runtime-' + [Guid]::NewGuid().ToString('N'))))
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
if (-not $fixture.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid fixture path.' }

function New-CrtFixture {
    param([string]$Root, [string]$Version, [string]$Architecture = 'x64')
    $directory = Join-Path $Root "$Version/$Architecture/Microsoft.VC143.CRT"
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    return $directory
}

function Assert-Equal {
    param($Expected, $Actual)
    if ($Expected -ne $Actual) { throw "Expected '$Expected', received '$Actual'." }
}

try {
    $redist = Join-Path $fixture 'MSVC'
    $older = New-CrtFixture $redist '14.9.99999'
    $newest = New-CrtFixture $redist '14.50.35710'
    $null = New-CrtFixture $redist '14.51.10000' 'arm64'
    $null = New-CrtFixture $redist 'v143'
    $null = New-CrtFixture $redist 'v145'
    $null = New-CrtFixture $redist 'not-a-runtime-version'
    Assert-Equal $newest (Find-VcRuntimeDirectory $redist)

    $aliases = Join-Path $fixture 'aliases-only'
    $null = New-CrtFixture $aliases 'v143'
    $latestAlias = New-CrtFixture $aliases 'v145'
    Assert-Equal $latestAlias (Find-VcRuntimeDirectory $aliases)

    $noX64 = Join-Path $fixture 'no-x64'
    $null = New-CrtFixture $noX64 '14.50.35710' 'arm64'
    Assert-Equal $null (Find-VcRuntimeDirectory $noX64)

    # Exercise the real payload script with isolated DLL fixtures, including its
    # explicit RuntimeDirectory override, allowlist and required-DLL validation.
    $required = @('msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll')
    foreach ($name in $required + @('concrt140.dll', 'unrelated.dll')) {
        [IO.File]::WriteAllText((Join-Path $newest $name), "fixture-$name")
    }
    $payload = Join-Path $fixture 'payload'
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    & $payloadScript -PayloadDirectory $payload -RuntimeDirectory $newest
    foreach ($name in $required + @('concrt140.dll')) {
        Assert-Equal "fixture-$name" ([IO.File]::ReadAllText((Join-Path $payload $name)))
    }
    if (Test-Path -LiteralPath (Join-Path $payload 'unrelated.dll')) { throw 'Unrelated DLL was bundled.' }

    Remove-Item -LiteralPath (Join-Path $newest 'vcruntime140_1.dll')
    $rejected = $false
    try { & $payloadScript -PayloadDirectory $payload -RuntimeDirectory $newest }
    catch {
        if ($_.Exception.Message -notlike '*VC runtime is missing vcruntime140_1.dll*') { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw 'Incomplete VC runtime was accepted.' }
    Write-Output 'Passed: numeric versions, toolset aliases, x64 discovery, payload copy and missing-DLL rejection. No installation was run.'
} finally {
    if (Test-Path -LiteralPath $fixture) {
        # fixture is resolved and verified inside the system temp directory above.
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}
